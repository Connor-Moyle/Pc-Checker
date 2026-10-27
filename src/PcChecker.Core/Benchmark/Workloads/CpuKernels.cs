using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;

namespace PcChecker.Core.Benchmark;

/// <summary>The four pieces of work the processor tests time. Each mixes integer or floating-point work differently.</summary>
public enum CpuKernel
{
    /// <summary>Deflate compression and decompression of generated text (integer, branches, memory).</summary>
    Compression,

    /// <summary>Sieve of Eratosthenes counting primes (integer, memory writes).</summary>
    PrimeSieve,

    /// <summary>Dense double-precision matrix multiply (floating point, vector units).</summary>
    MatrixMultiply,

    /// <summary>SHA-256 hashing (integer, uses the processor's SHA instructions when it has them).</summary>
    Sha256,
}

/// <summary>
/// How big one piece of work is. <see cref="Standard"/> takes roughly 20–80 ms per kernel on a 2024 desktop core;
/// tests use <see cref="Tiny"/>.
/// </summary>
public sealed record CpuWorkloadSize(int CompressionBytes, int SieveLimit, int MatrixSize, int HashBytes, int HashPasses)
{
    public static CpuWorkloadSize Standard { get; } = new(2 * 1024 * 1024, 8_000_000, 384, 4 * 1024 * 1024, 8);

    public static CpuWorkloadSize Tiny { get; } = new(64 * 1024, 100_000, 24, 64 * 1024, 1);

    /// <summary>Work done by one run of <paramref name="kernel"/> in the unit its throughput is reported in.</summary>
    public double WorkUnits(CpuKernel kernel) => kernel switch
    {
        CpuKernel.Compression => CompressionBytes / CpuKernels.BytesPerMegabyte,
        CpuKernel.PrimeSieve => SieveLimit / 1_000_000.0,
        CpuKernel.MatrixMultiply => 2.0 * MatrixSize * MatrixSize * (double)MatrixSize / 1e9,
        CpuKernel.Sha256 => (double)HashBytes * HashPasses / CpuKernels.BytesPerMegabyte,
        _ => 0,
    };
}

/// <summary>Read-only input shared by every thread: generated text, two matrices and a hash buffer.</summary>
public sealed class CpuWorkloadData
{
    private CpuWorkloadData(CpuWorkloadSize size, byte[] text, ulong textHash, double[] a, double[] b, byte[] hashInput)
    {
        Size = size;
        Text = text;
        TextHash = textHash;
        MatrixA = a;
        MatrixB = b;
        HashInput = hashInput;
    }

    public CpuWorkloadSize Size { get; }
    public byte[] Text { get; }
    public ulong TextHash { get; }
    public double[] MatrixA { get; }
    public double[] MatrixB { get; }
    public byte[] HashInput { get; }

    /// <summary>Builds the inputs for <paramref name="size"/>; the same seed always gives the same bytes.</summary>
    public static CpuWorkloadData Create(CpuWorkloadSize size, int seed = 2024)
    {
        ArgumentNullException.ThrowIfNull(size);
        var text = CpuKernels.GenerateText(size.CompressionBytes, seed);
        int n = size.MatrixSize;
        return new CpuWorkloadData(size, text, CpuKernels.Fnv1a(text),
            CpuKernels.CreateMatrix(n, 7, 13), CpuKernels.CreateMatrix(n, 11, 5), CpuKernels.GenerateBytes(size.HashBytes, seed + 1));
    }
}

/// <summary>Per-thread working memory, so threads never share anything they write.</summary>
public sealed class CpuScratch
{
    public CpuScratch(CpuWorkloadSize size)
    {
        ArgumentNullException.ThrowIfNull(size);
        Compressed = new MemoryStream(size.CompressionBytes / 2 + 4096);
        Decompressed = new byte[size.CompressionBytes];
        Sieve = new byte[CpuKernels.SieveSlots(size.SieveLimit)];
        Product = new double[size.MatrixSize * size.MatrixSize];
    }

    public MemoryStream Compressed { get; }
    public byte[] Decompressed { get; }
    public byte[] Sieve { get; }
    public double[] Product { get; }
}

/// <summary>
/// Deterministic CPU workloads. Every kernel returns a checksum that depends only on its input, so the same size
/// and seed give the same answer on every PC and every thread (a different answer means the processor made a mistake).
/// Pure .NET: runs anywhere.
/// </summary>
public static class CpuKernels
{
    public const double BytesPerMegabyte = 1024 * 1024;

    private static readonly string[] Words =
    [
        "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "processor", "memory", "drive", "graphics",
        "benchmark", "score", "fan", "cooling", "driver", "update", "window", "system", "power", "battery", "cache",
        "thread", "core", "clock", "voltage", "temperature", "result", "test", "fast", "slow", "data", "file", "game",
        "frame", "pixel", "shader", "network", "packet", "storage", "sector", "block", "page", "kernel", "service",
        "startup", "registry", "setting", "profile", "light", "colour", "sound", "screen", "mouse", "keyboard",
        "and", "with", "from", "into", "under", "after", "before", "while",
    ];

    /// <summary>Text-like bytes from a fixed vocabulary: compresses to about a third, like real documents.</summary>
    public static byte[] GenerateText(int bytes, int seed)
    {
        var output = new byte[Math.Max(0, bytes)];
        uint state = XorShiftSeed(seed);
        int pos = 0, line = 0;
        while (pos < output.Length)
        {
            state = XorShift(state);
            var word = Words[state % (uint)Words.Length];
            for (int i = 0; i < word.Length && pos < output.Length; i++)
            {
                output[pos++] = (byte)word[i];
            }

            line += word.Length + 1;
            if (pos < output.Length)
            {
                output[pos++] = line >= 72 ? (byte)'\n' : (state >> 8) % 11 == 0 ? (byte)',' : (byte)' ';
                if (line >= 72)
                {
                    line = 0;
                }
            }
        }

        return output;
    }

    /// <summary>Incompressible pseudo-random bytes (xorshift), for the hash input.</summary>
    public static byte[] GenerateBytes(int bytes, int seed)
    {
        var output = new byte[Math.Max(0, bytes)];
        uint state = XorShiftSeed(seed);
        for (int i = 0; i < output.Length; i++)
        {
            state = XorShift(state);
            output[i] = (byte)(state >> 13);
        }

        return output;
    }

    /// <summary>
    /// An n×n matrix (row-major) whose entries are multiples of 1/8 between −1 and 1, so every product and sum in the
    /// multiply is exact and the checksum cannot depend on rounding.
    /// </summary>
    public static double[] CreateMatrix(int n, int rowFactor, int columnFactor)
    {
        var m = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                m[i * n + j] = ((i * rowFactor + j * columnFactor) % 17 - 8) / 8.0;
            }
        }

        return m;
    }

    /// <summary>Compresses the text with Deflate, decompresses it and checks it came back unchanged.</summary>
    /// <returns>The text's FNV-1a hash when the round trip matched, 0 when it did not.</returns>
    public static ulong Compression(CpuWorkloadData data, CpuScratch scratch)
    {
        var stream = scratch.Compressed;
        stream.SetLength(0);
        using (var deflate = new DeflateStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data.Text, 0, data.Text.Length);
        }

        stream.Position = 0;
        int total = 0;
        using (var inflate = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true))
        {
            int read;
            while (total < scratch.Decompressed.Length && (read = inflate.Read(scratch.Decompressed, total, scratch.Decompressed.Length - total)) > 0)
            {
                total += read;
            }
        }

        return total == data.Text.Length && scratch.Decompressed.AsSpan(0, total).SequenceEqual(data.Text) ? data.TextHash : 0;
    }

    /// <summary>Slots an odd-only sieve needs for numbers up to <paramref name="limit"/>.</summary>
    public static int SieveSlots(int limit) => limit < 2 ? 1 : (limit + 1) / 2;

    /// <summary>Counts the primes up to and including <paramref name="limit"/> with an odd-only sieve.</summary>
    public static int CountPrimes(int limit, byte[] scratch)
    {
        ArgumentNullException.ThrowIfNull(scratch);
        if (limit < 2)
        {
            return 0;
        }

        int slots = SieveSlots(limit);
        if (scratch.Length < slots)
        {
            throw new ArgumentException("The sieve buffer is too small for the limit.", nameof(scratch));
        }

        // Slot i stands for the odd number 2i + 1; slot 0 (the number 1) is not prime.
        var composite = scratch.AsSpan(0, slots);
        composite.Clear();
        for (long i = 1; (2 * i + 1) * (2 * i + 1) <= limit; i++)
        {
            if (composite[(int)i] != 0)
            {
                continue;
            }

            long p = 2 * i + 1;
            for (long j = p * p / 2; j < slots; j += p)
            {
                composite[(int)j] = 1;
            }
        }

        int count = 1; // the number 2
        for (int i = 1; i < slots; i++)
        {
            count += composite[i] == 0 ? 1 : 0;
        }

        return count;
    }

    /// <summary>Product = A × B (row-major, n×n) using vector instructions; returns the bit pattern of the element sum.</summary>
    public static ulong MatrixMultiply(double[] a, double[] b, double[] product, int n)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(product);
        int width = Vector<double>.Count;
        for (int i = 0; i < n; i++)
        {
            var row = product.AsSpan(i * n, n);
            row.Clear();
            for (int k = 0; k < n; k++)
            {
                double aik = a[i * n + k];
                var bRow = b.AsSpan(k * n, n);
                var scale = new Vector<double>(aik);
                int j = 0;
                for (; j <= n - width; j += width)
                {
                    var sum = new Vector<double>(row.Slice(j)) + scale * new Vector<double>(bRow.Slice(j));
                    sum.CopyTo(row.Slice(j));
                }

                for (; j < n; j++)
                {
                    row[j] += aik * bRow[j];
                }
            }
        }

        double total = 0;
        for (int i = 0; i < n * n; i++)
        {
            total += product[i];
        }

        return (ulong)BitConverter.DoubleToInt64Bits(total);
    }

    /// <summary>Hashes the buffer <paramref name="passes"/> times with SHA-256 and folds the digests into a checksum.</summary>
    public static ulong Sha256(byte[] input, int passes)
    {
        ArgumentNullException.ThrowIfNull(input);
        Span<byte> digest = stackalloc byte[32];
        ulong checksum = 0;
        for (int p = 0; p < Math.Max(1, passes); p++)
        {
            SHA256.HashData(input, digest);
            checksum = BitOperations.RotateLeft(checksum, 7) ^ BitConverter.ToUInt64(digest);
        }

        return checksum;
    }

    /// <summary>Runs one kernel once on <paramref name="data"/> and returns its checksum.</summary>
    public static ulong Run(CpuKernel kernel, CpuWorkloadData data, CpuScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(scratch);
        return kernel switch
        {
            CpuKernel.Compression => Compression(data, scratch),
            CpuKernel.PrimeSieve => (ulong)CountPrimes(data.Size.SieveLimit, scratch.Sieve),
            CpuKernel.MatrixMultiply => MatrixMultiply(data.MatrixA, data.MatrixB, scratch.Product, data.Size.MatrixSize),
            CpuKernel.Sha256 => Sha256(data.HashInput, data.Size.HashPasses),
            _ => throw new ArgumentOutOfRangeException(nameof(kernel)),
        };
    }

    /// <summary>64-bit FNV-1a hash.</summary>
    public static ulong Fnv1a(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in bytes)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }

    private static uint XorShiftSeed(int seed) => (uint)seed * 2654435761u | 1u;

    private static uint XorShift(uint x)
    {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }
}

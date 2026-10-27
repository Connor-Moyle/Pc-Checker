using System.Text;

namespace PcChecker.Core.GameRuntimes;

/// <summary>
/// Reads which DLLs a Windows program (.exe or .dll) loads, straight from its file: the PE header says whether it is
/// 32-bit (PE32) or 64-bit (PE32+), and the import and delay-import tables list the DLL names. Only reads; nothing is
/// ever loaded or run. Every offset is checked against the file length and every loop is capped, so a damaged or
/// hostile file gives null or a short list, never an exception.
/// </summary>
public static class PeImportReader
{
    /// <summary>Most import descriptors read from one table.</summary>
    public const int MaxDescriptors = 2048;

    /// <summary>Most sections looked at.</summary>
    public const int MaxSections = 96;

    private const int MaxNameLength = 260;
    private const ushort Pe32Magic = 0x10B;
    private const ushort Pe32PlusMagic = 0x20B;
    private const int ImportDirectoryIndex = 1;
    private const int DelayImportDirectoryIndex = 13;

    /// <summary>Reads the imports of a file on disk. Returns null when it is not a readable PE file.</summary>
    public static PeImportInfo? ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Reads the imports from bytes (used by the tests).</summary>
    public static PeImportInfo? Read(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var stream = new MemoryStream(image, writable: false);
        return Read(stream);
    }

    /// <summary>Reads the imports from a seekable stream. Returns null when it is not a readable PE file.</summary>
    public static PeImportInfo? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            return new Reader(stream).Parse();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException or NotSupportedException or EndOfStreamException
                                       or IndexOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize);

    private sealed class Reader
    {
        private readonly Stream _stream;
        private readonly long _length;
        private Section[] _sections = [];
        private uint _sizeOfHeaders;

        public Reader(Stream stream)
        {
            _stream = stream;
            _length = stream.CanSeek ? stream.Length : throw new NotSupportedException("The stream must be seekable.");
        }

        public PeImportInfo? Parse()
        {
            if (_length < 0x40 || ReadUInt16(0) != 0x5A4D) // "MZ"
            {
                return null;
            }

            uint peOffset = ReadUInt32(0x3C);
            if (peOffset < 4 || peOffset > _length - 24 || ReadUInt32(peOffset) != 0x00004550) // "PE\0\0"
            {
                return null;
            }

            long fileHeader = peOffset + 4;
            ushort machine = ReadUInt16(fileHeader);
            ushort sectionCount = ReadUInt16(fileHeader + 2);
            ushort optionalSize = ReadUInt16(fileHeader + 16);
            ushort characteristics = ReadUInt16(fileHeader + 18);
            long optional = fileHeader + 20;
            if (optionalSize < 2 || optional + optionalSize > _length)
            {
                return null;
            }

            ushort magic = ReadUInt16(optional);
            bool plus;
            if (magic == Pe32Magic)
            {
                plus = false;
            }
            else if (magic == Pe32PlusMagic)
            {
                plus = true;
            }
            else
            {
                return null;
            }

            int countOffset = plus ? 108 : 92;
            int directoryOffset = plus ? 112 : 96;
            ulong imageBase = plus ? ReadUInt64Checked(optional + 24, optional + optionalSize) : ReadUInt32Checked(optional + 28, optional + optionalSize);
            _sizeOfHeaders = optionalSize >= 64 ? ReadUInt32(optional + 60) : 0;
            uint directoryCount = optionalSize >= countOffset + 4 ? ReadUInt32(optional + countOffset) : 0;
            directoryCount = Math.Min(directoryCount, (uint)Math.Max(0, (optionalSize - directoryOffset) / 8));

            _sections = ReadSections(optional + optionalSize, Math.Min((int)sectionCount, MaxSections));

            var imports = new List<string>();
            var delay = new List<string>();
            if (directoryCount > ImportDirectoryIndex)
            {
                uint rva = ReadUInt32(optional + directoryOffset + (8 * ImportDirectoryIndex));
                if (rva != 0)
                {
                    ReadImports(rva, imports);
                }
            }

            if (directoryCount > DelayImportDirectoryIndex)
            {
                uint rva = ReadUInt32(optional + directoryOffset + (8 * DelayImportDirectoryIndex));
                if (rva != 0)
                {
                    ReadDelayImports(rva, imageBase, delay);
                }
            }

            return new PeImportInfo(machine, plus, (characteristics & 0x2000) != 0, Distinct(imports), Distinct(delay));
        }

        private Section[] ReadSections(long start, int count)
        {
            var list = new List<Section>(count);
            for (int i = 0; i < count; i++)
            {
                long at = start + (40L * i);
                if (at + 40 > _length)
                {
                    break;
                }

                list.Add(new Section(ReadUInt32(at + 12), ReadUInt32(at + 8), ReadUInt32(at + 20), ReadUInt32(at + 16)));
            }

            return [.. list];
        }

        private void ReadImports(uint tableRva, List<string> names)
        {
            for (int i = 0; i < MaxDescriptors; i++)
            {
                long? at = RvaToOffset(tableRva + (uint)(20 * i));
                if (at is not { } offset || offset + 20 > _length)
                {
                    return;
                }

                uint originalFirstThunk = ReadUInt32(offset);
                uint nameRva = ReadUInt32(offset + 12);
                uint firstThunk = ReadUInt32(offset + 16);
                if (nameRva == 0)
                {
                    if (originalFirstThunk == 0 && firstThunk == 0)
                    {
                        return; // the all-zero terminator
                    }

                    continue;
                }

                if (ReadName(nameRva) is { } name)
                {
                    names.Add(name);
                }
            }
        }

        private void ReadDelayImports(uint tableRva, ulong imageBase, List<string> names)
        {
            for (int i = 0; i < MaxDescriptors; i++)
            {
                long? at = RvaToOffset(tableRva + (uint)(32 * i));
                if (at is not { } offset || offset + 32 > _length)
                {
                    return;
                }

                uint attributes = ReadUInt32(offset);
                uint nameValue = ReadUInt32(offset + 4);
                if (nameValue == 0)
                {
                    return;
                }

                // Old (Visual C++ 6) delay descriptors store virtual addresses instead of RVAs (attribute bit 0 clear).
                uint nameRva = nameValue;
                if ((attributes & 1) == 0 && imageBase != 0 && nameValue >= imageBase && nameValue - imageBase <= uint.MaxValue)
                {
                    nameRva = (uint)(nameValue - imageBase);
                }

                if (ReadName(nameRva) is { } name)
                {
                    names.Add(name);
                }
            }
        }

        private long? RvaToOffset(uint rva)
        {
            foreach (var s in _sections)
            {
                uint size = Math.Max(s.VirtualSize, s.RawSize);
                if (rva >= s.VirtualAddress && (ulong)rva < (ulong)s.VirtualAddress + size)
                {
                    ulong delta = rva - s.VirtualAddress;
                    if (delta >= s.RawSize)
                    {
                        return null; // in the zero-filled part of the section: nothing in the file
                    }

                    ulong offset = s.RawPointer + delta;
                    return offset < (ulong)_length ? (long)offset : null;
                }
            }

            // Data in the headers (tiny hand-made files) maps one to one.
            return rva < _sizeOfHeaders && rva < _length ? rva : null;
        }

        private string? ReadName(uint rva)
        {
            if (RvaToOffset(rva) is not { } offset)
            {
                return null;
            }

            int count = (int)Math.Min(MaxNameLength + 1, _length - offset);
            if (count <= 0)
            {
                return null;
            }

            var bytes = ReadBytes(offset, count);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end <= 0)
            {
                return null;
            }

            for (int i = 0; i < end; i++)
            {
                if (bytes[i] is < 0x20 or > 0x7E or (byte)'\\' or (byte)'/' or (byte)':' or (byte)'*' or (byte)'?' or (byte)'"' or (byte)'<' or (byte)'>' or (byte)'|')
                {
                    return null;
                }
            }

            return Encoding.ASCII.GetString(bytes, 0, end).Trim();
        }

        private static List<string> Distinct(List<string> names) =>
            names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private ulong ReadUInt64Checked(long at, long limit) => at + 8 <= limit ? BitConverter.ToUInt64(ReadBytes(at, 8)) : 0;

        private uint ReadUInt32Checked(long at, long limit) => at + 4 <= limit ? ReadUInt32(at) : 0;

        private ushort ReadUInt16(long at) => BitConverter.ToUInt16(ReadBytes(at, 2));

        private uint ReadUInt32(long at) => BitConverter.ToUInt32(ReadBytes(at, 4));

        private byte[] ReadBytes(long at, int count)
        {
            if (at < 0 || count < 0 || at > _length - count)
            {
                throw new EndOfStreamException();
            }

            var buffer = new byte[count];
            _stream.Seek(at, SeekOrigin.Begin);
            int done = 0;
            while (done < count)
            {
                int read = _stream.Read(buffer, done, count - done);
                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }

                done += read;
            }

            return buffer;
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using PcChecker.Core.Common;

namespace PcChecker.Core.Network;

/// <summary>
/// HTTP speed test against a Cloudflare-style speed server (<c>/__down?bytes=N</c> and <c>/__up</c>): idle latency,
/// download and upload over <see cref="Streams"/> parallel connections, and latency under load (bufferbloat). Each
/// transfer phase stops at its data budget or after <see cref="PhaseLimit"/>, and the first
/// <see cref="RampSeconds"/> of each phase are ignored while TCP ramps up. Cancellable; network failures become warnings.
/// </summary>
public static class NetworkSpeedTester
{
    public const int Streams = 4;
    public const double RampSeconds = 0.5;
    public const int IdleProbes = 10;
    public const int LoadedProbeIntervalMs = 250;

    /// <summary>
    /// Connections the latency probes may open. A probe times out after 5 s and one starts every
    /// <see cref="LoadedProbeIntervalMs"/>, so at most 20 are in flight: probes never wait in a queue for a free connection.
    /// </summary>
    public const int ProbeConnections = 24;
    public static readonly TimeSpan PhaseLimit = TimeSpan.FromSeconds(15);

    private const int BufferSize = 64 * 1024;
    private const long MaxChunkBytes = 25L * 1024 * 1024;
    private const long MaxUploadChunkBytes = 8L * 1024 * 1024;

    public static async Task<SpeedTestResult> RunAsync(NetworkSettings settings, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string server = (settings.SpeedTestServer ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(server) || Uri.CheckHostName(server) == UriHostNameType.Unknown)
        {
            server = NetworkSettings.DefaultServer;
        }

        long downloadBudget = Math.Clamp(settings.DownloadMegabytes, 1, 2000) * 1_000_000L;
        long uploadBudget = Math.Clamp(settings.UploadMegabytes, 1, 1000) * 1_000_000L;
        var warnings = new List<string>();
        var total = Stopwatch.StartNew();

        using var transfer = CreateClient(Streams + 2, Timeout.InfiniteTimeSpan);
        var probeStats = new ProbeStats();
        using var probe = CreateClient(ProbeConnections, TimeSpan.FromSeconds(5), probeStats.ConnectionOpened);

        // 1. Idle latency.
        progress?.Report(new ProgressUpdate("Measuring idle latency", 2, server));
        var idle = new List<double>();
        for (int i = 0; i <= IdleProbes; i++)
        {
            var (ms, _) = await ProbeAsync(probe, server, probeStats, cancellationToken).ConfigureAwait(false);
            // The first request also pays for DNS and the TLS handshake; leave it out.
            if (ms is { } v && i > 0)
            {
                idle.Add(v);
            }

            progress?.Report(new ProgressUpdate("Measuring idle latency", 2 + 8.0 * i / IdleProbes, ms is { } m ? $"{m:0} ms" : "no reply"));
        }

        if (idle.Count == 0)
        {
            warnings.Add(OfflineMessage(server, probeStats.LastError));
            return new SpeedTestResult(server, null, null, null, null, null, 100, 0, 0, total.Elapsed, "?", warnings);
        }

        // 2. Download with latency under load.
        var loaded = new List<double>();
        var download = await RunPhaseAsync("Download", 10, 45, downloadBudget, server, transfer, probe, probeStats, loaded, upload: false, warnings, progress, cancellationToken).ConfigureAwait(false);

        // 3. Upload with latency under load.
        var upload = await RunPhaseAsync("Upload", 55, 43, uploadBudget, server, transfer, probe, probeStats, loaded, upload: true, warnings, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ProgressUpdate("Finishing", 99));
        double? idleMs = Round(SpeedMath.Median(idle));
        double? loadedMs = Round(SpeedMath.Median(loaded));
        double probeLoss = probeStats.Sent == 0 ? 0 : Math.Round(100.0 * probeStats.Failed / probeStats.Sent, 1);
        return new SpeedTestResult(
            server,
            Round(download.Mbps),
            Round(upload.Mbps),
            idleMs,
            loadedMs,
            Round(SpeedMath.Jitter(idle)),
            probeLoss,
            download.Bytes,
            upload.Bytes,
            total.Elapsed,
            SpeedMath.BufferbloatGrade(idleMs, loadedMs),
            warnings);
    }

    private static async Task<(double? Mbps, long Bytes)> RunPhaseAsync(string phase, double startPercent, double spanPercent, long budget, string server,
        HttpClient transfer, HttpClient probe, ProbeStats probeStats, List<double> loaded, bool upload, List<string> warnings,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phaseCts.CancelAfter(PhaseLimit);
        long moved = 0;
        long reserved = 0;
        long chunk = Math.Clamp(budget / Streams, 1_000_000L, upload ? MaxUploadChunkBytes : MaxChunkBytes);
        byte[]? payload = null;
        if (upload)
        {
            payload = new byte[chunk];
            Random.Shared.NextBytes(payload); // Random data cannot be compressed along the way.
        }

        var errors = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        var samples = new List<(double Seconds, long Bytes)> { (0, 0) };

        async Task WorkerAsync()
        {
            while (!phaseCts.IsCancellationRequested)
            {
                long start = Interlocked.Add(ref reserved, chunk) - chunk;
                if (start >= budget)
                {
                    return;
                }

                long size = Math.Min(chunk, budget - start);
                try
                {
                    if (upload)
                    {
                        using var content = new CountingContent(payload!, (int)size, n => Interlocked.Add(ref moved, n));
                        using var response = await transfer.PostAsync($"https://{server}/__up", content, phaseCts.Token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                    }
                    else
                    {
                        using var response = await transfer.GetAsync($"https://{server}/__down?bytes={size}", HttpCompletionOption.ResponseHeadersRead, phaseCts.Token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        await using var stream = await response.Content.ReadAsStreamAsync(phaseCts.Token).ConfigureAwait(false);
                        var buffer = new byte[BufferSize];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, phaseCts.Token).ConfigureAwait(false)) > 0)
                        {
                            Interlocked.Add(ref moved, read);
                        }
                    }
                }
                catch (OperationCanceledException) when (phaseCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    lock (errors)
                    {
                        errors.Add(ex.Message);
                    }

                    return;
                }
            }
        }

        var workers = Enumerable.Range(0, Streams).Select(_ => Task.Run(WorkerAsync, CancellationToken.None)).ToList();
        var allDone = Task.WhenAll(workers);
        var nextProbe = TimeSpan.Zero;
        var probes = new List<Task>();
        while (!allDone.IsCompleted)
        {
            await Task.WhenAny(allDone, Task.Delay(100, CancellationToken.None)).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                break; // Drain the workers and probes below before reporting the cancellation.
            }

            double seconds = stopwatch.Elapsed.TotalSeconds;
            long bytes = Interlocked.Read(ref moved);
            samples.Add((seconds, bytes));

            if (stopwatch.Elapsed >= nextProbe && !allDone.IsCompleted)
            {
                nextProbe = stopwatch.Elapsed + TimeSpan.FromMilliseconds(LoadedProbeIntervalMs);
                probes.Add(ProbeIntoAsync(probe, server, probeStats, loaded, phaseCts.Token));
            }

            double percent = Math.Max(Math.Min(1, (double)bytes / budget), Math.Min(1, seconds / PhaseLimit.TotalSeconds));
            progress?.Report(new ProgressUpdate(phase, startPercent + spanPercent * percent, LiveRate(samples)));
        }

        // Workers stop as soon as phaseCts is cancelled (it is linked to the caller's token). Probes still in flight
        // belong to an idle line now; drop them, and wait for them all before the HTTP clients and phaseCts are disposed.
        await allDone.ConfigureAwait(false);
        samples.Add((stopwatch.Elapsed.TotalSeconds, Interlocked.Read(ref moved)));
        phaseCts.Cancel();
        await Task.WhenAll(probes).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (stopwatch.Elapsed >= PhaseLimit - TimeSpan.FromMilliseconds(200) && Interlocked.Read(ref moved) < budget)
        {
            warnings.Add($"{phase} stopped at the {PhaseLimit.TotalSeconds:0} s limit after {ByteSize.Format(moved)} (budget {ByteSize.Format(budget)}).");
        }

        if (errors.Count > 0)
        {
            warnings.Add($"{phase}: {errors.Count} transfer{(errors.Count == 1 ? string.Empty : "s")} failed ({errors[0]}). The result may be lower than your real speed.");
        }

        long total = Interlocked.Read(ref moved);
        return (total > 0 ? SpeedMath.WindowedMbps(samples, RampSeconds) : null, total);
    }

    private static async Task ProbeIntoAsync(HttpClient probe, string server, ProbeStats stats, List<double> sink, CancellationToken cancellationToken)
    {
        try
        {
            var (ms, newConnection) = await ProbeAsync(probe, server, stats, cancellationToken).ConfigureAwait(false);
            // A probe that had to open a connection also timed the TCP and TLS handshake; that is not queueing delay.
            if (ms is { } v && !newConnection)
            {
                lock (sink)
                {
                    sink.Add(v);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The phase ended while this probe was in flight; it does not count.
        }
    }

    /// <summary>
    /// Time to the response headers of an empty download, in ms (null when it failed), and whether a probe
    /// connection was opened while it ran (the time then includes a handshake; with probes overlapping it may have
    /// been another probe's connection, so this errs on the side of dropping a sample).
    /// </summary>
    private static async Task<(double? Ms, bool NewConnection)> ProbeAsync(HttpClient client, string server, ProbeStats stats, CancellationToken cancellationToken)
    {
        int connectionsBefore = stats.ConnectionsOpened;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync($"https://{server}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            double ms = stopwatch.Elapsed.TotalMilliseconds;
            stats.Record(response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", response.StatusCode);
            return (response.IsSuccessStatusCode ? ms : null, stats.ConnectionsOpened != connectionsBefore);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            stats.Record(ex is TaskCanceledException ? "timed out" : ex.Message, null);
            return (null, stats.ConnectionsOpened != connectionsBefore);
        }
    }

    private static string LiveRate(List<(double Seconds, long Bytes)> samples)
    {
        // Rate over the last second for the live readout.
        var last = samples[^1];
        var earlier = samples.LastOrDefault(s => s.Seconds <= last.Seconds - 1.0);
        if (earlier == default)
        {
            earlier = samples[0];
        }

        var mbps = SpeedMath.Mbps(last.Bytes - earlier.Bytes, TimeSpan.FromSeconds(Math.Max(0.001, last.Seconds - earlier.Seconds)));
        return $"{mbps ?? 0:0} Mbps · {ByteSize.Format(last.Bytes)}";
    }

    private static string OfflineMessage(string server, string? error)
    {
        string detail = error is null ? string.Empty : $" ({error})";
        if (error?.Contains("407", StringComparison.Ordinal) == true)
        {
            return $"A proxy server asked for a sign-in, so {server} could not be reached{detail}. Run the test on a network without a proxy.";
        }

        return $"Could not reach {server}{detail}. Check that you are online and that a firewall, proxy or filter is not blocking it, then try again.";
    }

    private static HttpClient CreateClient(int connections, TimeSpan timeout, Action? onConnect = null)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = connections,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseCookies = false,
        };
        if (onConnect is not null)
        {
            // Same as the default connect, plus a count so probes can tell when they paid for a new connection.
            handler.ConnectCallback = async (context, ct) =>
            {
                onConnect();
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(NetworkSystemInfo.UserAgent);
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return client;
    }

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 1) : null;

    private sealed class ProbeStats
    {
        private int _sent;
        private int _failed;
        private int _connections;

        public int Sent => Volatile.Read(ref _sent);
        public int ConnectionsOpened => Volatile.Read(ref _connections);

        public void ConnectionOpened() => Interlocked.Increment(ref _connections);

        public int Failed => Volatile.Read(ref _failed);
        public string? LastError { get; private set; }

        public void Record(string? error, HttpStatusCode? status)
        {
            Interlocked.Increment(ref _sent);
            if (error is not null)
            {
                Interlocked.Increment(ref _failed);
                LastError = status is null ? error : $"{error}";
            }
        }
    }

    /// <summary>Request body that reports how many bytes have been handed to the network so upload speed can be measured live.</summary>
    private sealed class CountingContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly int _length;
        private readonly Action<int> _onWritten;

        public CountingContent(byte[] payload, int length, Action<int> onWritten)
        {
            _payload = payload;
            _length = Math.Min(length, payload.Length);
            _onWritten = onWritten;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < _length)
            {
                int count = Math.Min(BufferSize, _length - offset);
                await stream.WriteAsync(_payload.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                offset += count;
                _onWritten(count);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}

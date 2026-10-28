using PcChecker.Core.Common;

namespace PcChecker.Core.Network.Resolvers;

/// <summary>How one query went.</summary>
public enum DnsExchangeStatus
{
    Answered,
    Timeout,
    NetworkError,
}

/// <summary>The raw outcome of one query: the response bytes (when one came) and the time it took.</summary>
public sealed record DnsExchange(DnsExchangeStatus Status, byte[]? Response, double ElapsedMs);

/// <summary>Sends one query packet to one server and waits for the reply. Real UDP in the app, simulated in demo mode and tests.</summary>
public interface IDnsTransport
{
    Task<DnsExchange> ExchangeAsync(string server, byte[] query, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Settings for one benchmark run; the defaults finish in well under 30 seconds on a normal connection.</summary>
public sealed record DnsBenchmarkOptions
{
    /// <summary>Measured passes over the popular names (after one warm-up pass that is not timed).</summary>
    public int CachedRounds { get; init; } = 2;

    /// <summary>Made-up names per server for the uncached test.</summary>
    public int UncachedQueries { get; init; } = 6;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Servers tested at the same time.</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>The whole run stops after this long; servers not finished by then show what they managed.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A server that misses this many lookups in a row at the start is marked unreachable and skipped.</summary>
    public int GiveUpAfterFailures { get; init; } = 4;

    public IReadOnlyList<string> PopularNames { get; init; } = DnsQueryNames.Popular;

    public IReadOnlyList<string> UncachedBases { get; init; } = DnsQueryNames.UncachedBases;

    /// <summary>Seed for the made-up names and query ids (tests); null uses a random seed.</summary>
    public int? Seed { get; init; }
}

/// <summary>The measurements for one server.</summary>
public sealed record DnsServerResult(
    DnsBenchmarkTarget Target,
    DnsTimingSummary Cached,
    DnsTimingSummary Uncached,
    int Sent,
    int Answered,
    int Timeouts,
    int Errors,
    bool? ValidatesDnssec,
    bool Unreachable,
    bool Partial)
{
    /// <summary>The share of lookups answered, 0 to 1.</summary>
    public double Reliability => DnsTimingStats.Reliability(Answered, Sent);
}

/// <summary>A finished (or stopped) benchmark run.</summary>
public sealed record DnsBenchmarkReport(IReadOnlyList<DnsServerResult> Results, DateTimeOffset Finished, TimeSpan Duration, bool StoppedEarly)
{
    public static DnsBenchmarkReport Empty { get; } = new([], DateTimeOffset.MinValue, TimeSpan.Zero, false);
}

/// <summary>
/// Runs the DNS benchmark over an <see cref="IDnsTransport"/>: for each server a warm-up pass over the popular names,
/// timed passes over the same names (A and AAAA, so answers come from the cache), made-up names that cannot be cached,
/// and one DNSSEC probe. Servers run a few at a time; the run is cancellable and stops at the deadline.
/// </summary>
public sealed class DnsBenchmarkRunner
{
    private readonly IDnsTransport _transport;
    private readonly DnsBenchmarkOptions _options;

    public DnsBenchmarkRunner(IDnsTransport transport, DnsBenchmarkOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new DnsBenchmarkOptions();
    }

    public async Task<DnsBenchmarkReport> RunAsync(IReadOnlyList<DnsBenchmarkTarget> targets, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var started = DateTimeOffset.Now;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Deadline);
        var results = new DnsServerResult?[targets.Count];
        int done = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, _options.Parallelism));
        var seed = _options.Seed ?? Random.Shared.Next();

        var tasks = targets.Select(async (target, index) =>
        {
            try
            {
                await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                results[index] = Empty(target, partial: true);
                return;
            }

            try
            {
                results[index] = await TestServerAsync(target, new Random(seed + index), deadline.Token, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
                int finished = Interlocked.Increment(ref done);
                progress?.Report(new ProgressUpdate("Testing DNS servers", 100.0 * finished / Math.Max(1, targets.Count), target.ProviderName ?? target.Address));
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        bool stoppedEarly = deadline.IsCancellationRequested;
        return new DnsBenchmarkReport(results.Select((r, i) => r ?? Empty(targets[i], partial: true)).ToList(), started, watch.Elapsed, stoppedEarly);
    }

    private async Task<DnsServerResult> TestServerAsync(DnsBenchmarkTarget target, Random random, CancellationToken deadline, CancellationToken user)
    {
        var tally = new Tally();
        try
        {
            // Warm-up: put the popular names in the server's cache. Counted for reliability, not timed.
            foreach (var name in _options.PopularNames)
            {
                foreach (var type in new[] { DnsRecordType.A, DnsRecordType.Aaaa })
                {
                    var outcome = await QueryAsync(target.Address, name, type, false, random, deadline).ConfigureAwait(false);
                    tally.Add(outcome, null);
                    if (tally.Answered == 0 && tally.Sent >= _options.GiveUpAfterFailures)
                    {
                        return tally.ToResult(target, unreachable: true, partial: false);
                    }
                }
            }

            for (int round = 0; round < _options.CachedRounds; round++)
            {
                foreach (var name in _options.PopularNames)
                {
                    foreach (var type in new[] { DnsRecordType.A, DnsRecordType.Aaaa })
                    {
                        tally.Add(await QueryAsync(target.Address, name, type, false, random, deadline).ConfigureAwait(false), tally.Cached);
                    }
                }
            }

            foreach (var name in DnsQueryNames.UncachedNames(random, _options.UncachedQueries, _options.UncachedBases))
            {
                tally.Add(await QueryAsync(target.Address, name, DnsRecordType.A, false, random, deadline).ConfigureAwait(false), tally.Uncached);
            }

            var dnssec = await QueryAsync(target.Address, DnsQueryNames.DnssecProbeName, DnsRecordType.A, true, random, deadline).ConfigureAwait(false);
            tally.Add(dnssec, null);
            tally.Dnssec = dnssec.Response is { IsUsableAnswer: true } r ? r.AuthenticatedData : null;
            return tally.ToResult(target, unreachable: false, partial: false);
        }
        catch (OperationCanceledException) when (!user.IsCancellationRequested)
        {
            return tally.ToResult(target, unreachable: tally.Sent > 0 && tally.Answered == 0, partial: true);
        }
    }

    private async Task<(DnsExchangeStatus Status, DnsResponse? Response, double ElapsedMs)> QueryAsync(
        string server, string name, ushort type, bool dnssec, Random random, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ushort id = (ushort)random.Next(0, 65536);
        byte[] query = DnsQueryBuilder.Build(id, name, type, recursionDesired: true, dnssecOk: dnssec);
        DnsExchange exchange;
        try
        {
            exchange = await _transport.ExchangeAsync(server, query, _options.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (DnsExchangeStatus.NetworkError, null, 0);
        }

        if (exchange.Status != DnsExchangeStatus.Answered || exchange.Response is null)
        {
            return (exchange.Status == DnsExchangeStatus.Answered ? DnsExchangeStatus.NetworkError : exchange.Status, null, exchange.ElapsedMs);
        }

        // A reply for another query (wrong id or name) or one that is not a usable answer counts as an error.
        if (!DnsResponseParser.TryParse(exchange.Response, out var response) || response is null || response.Id != id
            || !string.Equals(response.QuestionName, name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
        {
            return (DnsExchangeStatus.NetworkError, null, exchange.ElapsedMs);
        }

        return (DnsExchangeStatus.Answered, response, exchange.ElapsedMs);
    }

    private static DnsServerResult Empty(DnsBenchmarkTarget target, bool partial) =>
        new(target, DnsTimingSummary.Empty, DnsTimingSummary.Empty, 0, 0, 0, 0, null, false, partial);

    private sealed class Tally
    {
        public List<double> Cached { get; } = [];
        public List<double> Uncached { get; } = [];
        public int Sent { get; private set; }
        public int Answered { get; private set; }
        public int Timeouts { get; private set; }
        public int Errors { get; private set; }
        public bool? Dnssec { get; set; }

        public void Add((DnsExchangeStatus Status, DnsResponse? Response, double ElapsedMs) outcome, List<double>? timings)
        {
            Sent++;
            if (outcome.Status == DnsExchangeStatus.Timeout)
            {
                Timeouts++;
            }
            else if (outcome.Response is { IsUsableAnswer: true })
            {
                Answered++;
                timings?.Add(outcome.ElapsedMs);
            }
            else
            {
                Errors++;
            }
        }

        public DnsServerResult ToResult(DnsBenchmarkTarget target, bool unreachable, bool partial) =>
            new(target, DnsTimingStats.Summarize(Cached), DnsTimingStats.Summarize(Uncached), Sent, Answered, Timeouts, Errors, Dnssec, unreachable, partial);
    }
}

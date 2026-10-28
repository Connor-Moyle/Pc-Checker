namespace PcChecker.Core.Network.Resolvers;

/// <summary>What the benchmark suggests.</summary>
public enum DnsAdviceKind
{
    /// <summary>No server answered enough lookups to judge.</summary>
    NoData,

    /// <summary>The DNS in use is already about as fast as the best; switching would not be noticed.</summary>
    KeepCurrent,

    /// <summary>A public (or the custom) server is clearly faster or more reliable.</summary>
    Switch,

    /// <summary>The router's own DNS beat the servers set by hand: go back to automatic.</summary>
    UseAutomatic,
}

/// <summary>
/// The advice. <see cref="Best"/> is the fastest reliable server that filters the same as the current one (usually: not at all); <see cref="Current"/> the server in
/// use now (null when it was not tested); <see cref="SavingMs"/> how much faster a typical lookup gets by switching;
/// <see cref="FilteringAlternatives"/> the fastest reliable server of each service that blocks something.
/// </summary>
public sealed record DnsRecommendation(
    DnsAdviceKind Kind,
    DnsServerResult? Best,
    DnsServerResult? Current,
    double? SavingMs,
    bool CurrentUnreliable,
    IReadOnlyList<DnsServerResult> FilteringAlternatives);

/// <summary>Scores benchmark results and turns them into one plain recommendation. Pure.</summary>
public static class DnsRecommendationRules
{
    /// <summary>A server must answer at least this share of lookups to be recommended.</summary>
    public const double MinReliability = 0.95;

    /// <summary>A server needs at least this many timed answers to be judged.</summary>
    public const int MinTimedAnswers = 5;

    /// <summary>How much the uncached time counts next to the cached one: most lookups are answered from the cache.</summary>
    public const double UncachedWeight = 0.25;

    /// <summary>Switching is suggested only when it saves at least this much per lookup…</summary>
    public const double MinSavingMs = 5;

    /// <summary>…and at least this share of the current time.</summary>
    public const double MinSavingShare = 0.15;

    /// <summary>
    /// One number per server, lower is better: the median cached time plus a quarter of the median uncached time.
    /// Null when the server gave too few answers.
    /// </summary>
    public static double? Score(DnsServerResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Unreachable || result.Cached.MedianMs is not { } cached || result.Cached.Count < MinTimedAnswers)
        {
            return null;
        }

        return cached + UncachedWeight * (result.Uncached.MedianMs ?? 0);
    }

    public static bool IsReliable(DnsServerResult result) =>
        Score(result) is not null && result.Reliability >= MinReliability;

    public static DnsRecommendation Recommend(IReadOnlyList<DnsServerResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var reliable = results.Where(IsReliable).OrderBy(r => Score(r)).ToList();

        // The server in use: Windows asks the first one first, so the first current server stands for "your DNS".
        var current = results.FirstOrDefault(r => r.Target.IsCurrent);
        bool currentUnreliable = current is not null && !IsReliable(current) && current.Sent > 0;

        // Someone who chose a filtering service (Quad9, AdGuard…) chose it for the filtering: compare like with like.
        var wanted = current?.Target.Filtering ?? DnsFiltering.None;
        var alternatives = reliable
            .Where(r => r.Target.Filtering != DnsFiltering.None && r.Target.Filtering != wanted && !r.Target.IsCurrent)
            .GroupBy(r => r.Target.ProviderId ?? r.Target.Address)
            .Select(g => g.First())
            .ToList();

        var best = reliable.FirstOrDefault(r => r.Target.Filtering == wanted || r.Target.IsCurrent);
        if (best is null)
        {
            return new DnsRecommendation(DnsAdviceKind.NoData, null, current, null, currentUnreliable, alternatives);
        }

        if (current is null || IsSameService(best, current))
        {
            return new DnsRecommendation(current is null ? DnsAdviceKind.Switch : DnsAdviceKind.KeepCurrent, best, current, null, currentUnreliable, alternatives);
        }

        double? saving = Score(current) is { } currentScore ? currentScore - Score(best)!.Value : null;
        bool worthIt = currentUnreliable
            || saving is null
            || (saving >= MinSavingMs && saving >= MinSavingShare * Score(current)!.Value);
        if (!worthIt)
        {
            return new DnsRecommendation(DnsAdviceKind.KeepCurrent, current, current, saving, currentUnreliable, alternatives);
        }

        var kind = best.Target.IsRouter && !best.Target.IsCurrent ? DnsAdviceKind.UseAutomatic : DnsAdviceKind.Switch;
        return new DnsRecommendation(kind, best, current, saving, currentUnreliable, alternatives);
    }

    /// <summary>True when both results are servers of the same service (or the same address).</summary>
    public static bool IsSameService(DnsServerResult a, DnsServerResult b) =>
        a.Target.Address.Equals(b.Target.Address, StringComparison.OrdinalIgnoreCase)
        || (a.Target.ProviderId is not null && a.Target.ProviderId.Equals(b.Target.ProviderId, StringComparison.OrdinalIgnoreCase))
        || (a.Target.IsCurrent && b.Target.IsCurrent);
}

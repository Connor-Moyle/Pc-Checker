namespace PcChecker.Core.AppHistory;

/// <summary>One row of SRUM's application resource usage table ({5C8CF1C7-7257-4F13-B223-970EF5939312}), raw.</summary>
public sealed record SrumResourceRow(DateTimeOffset Time, int AppId, int? UserId, long ForegroundCycles, long BackgroundCycles,
    long ForegroundBytesRead, long ForegroundBytesWritten, long BackgroundBytesRead, long BackgroundBytesWritten);

/// <summary>One row of SRUM's network data usage table ({973F5D5C-1D90-4944-BE8E-24B94231A174}), raw.</summary>
public sealed record SrumNetworkRow(DateTimeOffset Time, int AppId, int? UserId, long InterfaceLuid, long BytesSent, long BytesReceived);

/// <summary>What was read from a copy of the SRUM database, before it is turned into figures.</summary>
public sealed record SrumRawData(IReadOnlyList<SrumIdEntry> Ids, IReadOnlyList<SrumResourceRow> Resources, IReadOnlyList<SrumNetworkRow> Network)
{
    public int SkippedRows { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Turns SRUM's raw rows into <see cref="AppUsageRecord"/>s: cycles become processor time (see
/// <see cref="SrumConvert.CyclesToCpuTime"/>), foreground and background figures are added together, network rows are
/// matched to the hour's resource row of the same app, data over mobile broadband counts as metered, and every app id
/// becomes an <see cref="AppIdentity"/> with a drive-letter path. Rows whose app id is not in the id map are kept under
/// "Unknown app (id)". Pure.
/// </summary>
public static class SrumRecordBuilder
{
    public static (IReadOnlyList<AppUsageRecord> Records, IReadOnlyDictionary<string, AppIdentity> Apps) Build(
        SrumRawData raw, double? processorMhz, IReadOnlyDictionary<string, string> deviceToDrive)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(deviceToDrive);
        double mhz = SrumConvert.EffectiveMhz(processorMhz);
        var ids = SrumIdMapDecoder.ToLookup(raw.Ids);
        var apps = new Dictionary<string, AppIdentity>(StringComparer.OrdinalIgnoreCase);
        var keyById = new Dictionary<int, string>();

        string KeyOf(int appId)
        {
            if (keyById.TryGetValue(appId, out var known))
            {
                return known;
            }

            AppIdentity identity = ids.TryGetValue(appId, out var entry) && entry.Kind != SrumIdKind.User
                ? AppNames.Describe(entry, deviceToDrive)
                : new AppIdentity("srum-id-" + appId.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Unknown app ({appId})", null, null);
            apps.TryAdd(identity.AppKey, identity);
            keyById[appId] = identity.AppKey;
            return identity.AppKey;
        }

        string? SidOf(int? userId) =>
            userId is { } id && ids.TryGetValue(id, out var entry) && entry.Kind == SrumIdKind.User ? entry.Value : null;

        var records = new List<AppUsageRecord>(raw.Resources.Count + raw.Network.Count);
        foreach (var row in raw.Resources)
        {
            long cycles = Clamp(row.ForegroundCycles) + Clamp(row.BackgroundCycles);
            records.Add(new AppUsageRecord(KeyOf(row.AppId), row.Time, row.Time,
                SrumConvert.CyclesToCpuTime(cycles, mhz).TotalSeconds, null, null, null,
                Clamp(row.ForegroundBytesRead) + Clamp(row.BackgroundBytesRead),
                Clamp(row.ForegroundBytesWritten) + Clamp(row.BackgroundBytesWritten))
            { UserSid = SidOf(row.UserId) });
        }

        foreach (var row in raw.Network)
        {
            long sent = Clamp(row.BytesSent);
            long received = Clamp(row.BytesReceived);
            records.Add(new AppUsageRecord(KeyOf(row.AppId), row.Time, row.Time, 0, sent, received,
                SrumConvert.IsMeteredInterface(row.InterfaceLuid) ? sent + received : 0, null, null)
            { UserSid = SidOf(row.UserId) });
        }

        records.Sort((a, b) => a.Start.CompareTo(b.Start));
        return (records, apps);
    }

    private static long Clamp(long value) => Math.Max(0, value);
}

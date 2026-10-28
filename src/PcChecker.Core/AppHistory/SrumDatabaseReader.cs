using System.Runtime.Versioning;
using Microsoft.Isam.Esent;
using Microsoft.Isam.Esent.Interop;
using PcChecker.Core.Common;
using static PcChecker.Core.AppHistory.AppHistoryText;

namespace PcChecker.Core.AppHistory;

/// <summary>A failed read of Windows' records, with the reason already classified.</summary>
public sealed class SrumReadException(SrumFailureKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public SrumFailureKind Kind { get; } = kind;
}

/// <summary>
/// Reads a <em>copy</em> of the SRUM database (<c>SRUDB.dat</c>, an ESE file) through esent.dll with the managed
/// ManagedEsent wrapper. The copy is opened read-only with recovery off, in a private ESE instance whose system, log
/// and temporary files all go into <paramref name="workDirectory"/>, so nothing is written next to Windows' own
/// database. Every column is read as raw bytes and decoded by <see cref="SrumConvert"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SrumDatabaseReader
{
    public const string ResourceUsageTable = "{5C8CF1C7-7257-4F13-B223-970EF5939312}";
    public const string NetworkUsageTable = "{973F5D5C-1D90-4944-BE8E-24B94231A174}";
    public const string IdMapTable = "SruDbIdMapTable";

    private static readonly object EngineGate = new();

    public static SrumRawData Read(string databasePath, string workDirectory, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        // esent.dll keeps process-wide settings (the page size); one reader at a time.
        lock (EngineGate)
        {
            try
            {
                return ReadCore(databasePath, workDirectory, progress, cancellationToken);
            }
            catch (EsentErrorException ex)
            {
                var kind = SrumErrorClassifier.Classify((int)ex.Error);
                if (kind == SrumFailureKind.Unknown)
                {
                    kind = SrumFailureKind.Corrupt;
                }

                throw new SrumReadException(kind, SrumErrorClassifier.Message(kind), ex);
            }
            catch (EsentException ex)
            {
                throw new SrumReadException(SrumFailureKind.Corrupt, SrumErrorClassifier.Message(SrumFailureKind.Corrupt), ex);
            }
        }
    }

    private static SrumRawData ReadCore(string databasePath, string workDirectory, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        Api.JetGetDatabaseFileInfo(databasePath, out int pageSize, JET_DbInfo.PageSize);
        try
        {
            SystemParameters.DatabasePageSize = pageSize;
        }
        catch (EsentErrorException)
        {
            // Already set for this process by an earlier read with the same size; a different size fails at attach.
        }

        var folder = workDirectory.TrimEnd('\\') + "\\";
        using var instance = new Instance("PcCheckerSrum" + Guid.NewGuid().ToString("N")[..8], "PC Checker app history");
        instance.Parameters.Recovery = false;
        instance.Parameters.CircularLog = true;
        instance.Parameters.NoInformationEvent = true;
        instance.Parameters.CreatePathIfNotExist = true;
        instance.Parameters.SystemDirectory = folder;
        instance.Parameters.LogFileDirectory = folder;
        instance.Parameters.TempDirectory = folder;
        instance.Parameters.BaseName = "pcc";
        instance.Init();

        using var session = new Session(instance);
        Api.JetAttachDatabase(session, databasePath, AttachDatabaseGrbit.ReadOnly);
        Api.JetOpenDatabase(session, databasePath, null, out var dbid, OpenDatabaseGrbit.ReadOnly);
        try
        {
            var tables = Api.GetTableNames(session, dbid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!tables.Contains(IdMapTable) || !tables.Contains(ResourceUsageTable))
            {
                throw new SrumReadException(SrumFailureKind.Unsupported, SrumErrorClassifier.Message(SrumFailureKind.Unsupported));
            }

            var warnings = new List<string>();
            int skipped = 0;
            progress?.Report(new ProgressUpdate(T("Reading the list of apps…"), 55));
            var ids = new List<SrumIdEntry>();
            ForEachRow(session, dbid, IdMapTable, ct, row =>
            {
                var index = SrumConvert.ReadInteger(row("IdIndex"));
                var entry = index is { } i ? SrumIdMapDecoder.Decode((int)i, SrumConvert.ReadInteger(row("IdType")), row("IdBlob")) : null;
                if (entry is null)
                {
                    skipped++;
                }
                else
                {
                    ids.Add(entry);
                }
            });

            progress?.Report(new ProgressUpdate(T("Reading processor and disk use…"), 65));
            var resources = new List<SrumResourceRow>();
            ForEachRow(session, dbid, ResourceUsageTable, ct, row =>
            {
                var time = SrumConvert.ReadOleDate(row("TimeStamp"));
                var app = SrumConvert.ReadInteger(row("AppId"));
                if (time is null || app is null)
                {
                    skipped++;
                    return;
                }

                resources.Add(new SrumResourceRow(time.Value, (int)app.Value, (int?)SrumConvert.ReadInteger(row("UserId")),
                    Number(row("ForegroundCycleTime")), Number(row("BackgroundCycleTime")),
                    Number(row("ForegroundBytesRead")), Number(row("ForegroundBytesWritten")),
                    Number(row("BackgroundBytesRead")), Number(row("BackgroundBytesWritten"))));
            });

            var network = new List<SrumNetworkRow>();
            if (tables.Contains(NetworkUsageTable))
            {
                progress?.Report(new ProgressUpdate(T("Reading network use…"), 80));
                ForEachRow(session, dbid, NetworkUsageTable, ct, row =>
                {
                    var time = SrumConvert.ReadOleDate(row("TimeStamp"));
                    var app = SrumConvert.ReadInteger(row("AppId"));
                    if (time is null || app is null)
                    {
                        skipped++;
                        return;
                    }

                    network.Add(new SrumNetworkRow(time.Value, (int)app.Value, (int?)SrumConvert.ReadInteger(row("UserId")),
                        Number(row("InterfaceLuid")), Number(row("BytesSent")), Number(row("BytesRecvd"))));
                });
            }
            else
            {
                warnings.Add(T("Windows has no network records on this PC."));
            }

            return new SrumRawData(ids, resources, network) { SkippedRows = skipped, Warnings = warnings };
        }
        finally
        {
            try
            {
                Api.JetCloseDatabase(session, dbid, CloseDatabaseGrbit.None);
                Api.JetDetachDatabase(session, databasePath);
            }
            catch (EsentException)
            {
                // The instance is torn down next anyway.
            }
        }
    }

    /// <summary>Calls <paramref name="onRow"/> for every row; the callback reads a column's raw bytes by name (null when absent).</summary>
    private static void ForEachRow(JET_SESID session, JET_DBID dbid, string tableName, CancellationToken ct, Action<Func<string, byte[]?>> onRow)
    {
        using var table = new Table(session, dbid, tableName, OpenTableGrbit.ReadOnly);
        var columns = new Dictionary<string, JET_COLUMNID>(Api.GetColumnDictionary(session, table), StringComparer.OrdinalIgnoreCase);
        byte[]? Column(string name) => columns.TryGetValue(name, out var id) ? Api.RetrieveColumn(session, table, id) : null;

        if (!Api.TryMoveFirst(session, table))
        {
            return;
        }

        int count = 0;
        do
        {
            if ((++count & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            onRow(Column);
        }
        while (Api.TryMoveNext(session, table));
    }

    private static long Number(byte[]? value) => SrumConvert.ReadInteger(value) ?? 0;
}

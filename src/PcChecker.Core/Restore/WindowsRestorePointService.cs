using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Settings;

namespace PcChecker.Core.Restore;

/// <summary>
/// Windows implementation of <see cref="IRestorePointService"/>: the <c>SystemRestore</c> WMI class for listing and
/// creating points, the registry for the on/off state and the 24-hour frequency override, and
/// <c>Enable-ComputerRestore</c> to turn System Protection on. Every call degrades to a failed result instead of throwing.
/// </summary>
public sealed class WindowsRestorePointService : IRestorePointService
{
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValue = "SystemRestorePointCreationFrequency";
    private const string WmiPath = @"\\.\root\default:SystemRestore";

    /// <summary>SystemRestore.CreateRestorePoint accepts up to 256 characters (MAX_DESC_W).</summary>
    public const int MaxDescriptionLength = 256;

    public const string NoNewPointMessage =
        "Windows reported success but no new restore point appeared. Windows normally creates only one restore point per 24 hours; " +
        "turn on \"Allow more than one restore point per day\" in Settings → Restore points, or wait and try again";

    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(45);

    private readonly ISettingsService _settings;
    private readonly ILogger<WindowsRestorePointService> _logger;

    public WindowsRestorePointService(ISettingsService settings, ILogger<WindowsRestorePointService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return false;
        }

        var fromRegistry = ReadEnabledFromRegistry();
        if (fromRegistry is not null)
        {
            return fromRegistry.Value;
        }

        // Registry inconclusive (fresh install, policy-managed): ask PowerShell whether System Restore answers at all.
        try
        {
            var result = await PowerShellRunner.RunAsync("$ErrorActionPreference = 'Stop'; Get-ComputerRestorePoint | Out-Null; 'ok'", ListTimeout, cancellationToken).ConfigureAwait(false);
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not query System Restore state via PowerShell.");
            return false;
        }
    }

    public async Task<OperationResult> EnableAsync(string drive, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("System Restore is only available on Windows.");
        }

        var root = NormalizeDrive(drive);
        var script = $"$ErrorActionPreference = 'Stop'; Enable-ComputerRestore -Drive {PowerShellRunner.Quote(root)}; 'ok'";
        var result = await PowerShellRunner.RunAsync(script, CreateTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            var error = string.IsNullOrWhiteSpace(result.StdErr) ? $"PowerShell exit code {result.ExitCode}" : result.StdErr.Split('\n')[0].Trim();
            _logger.LogWarning("Enable-ComputerRestore failed for {Drive}: {Error}", root, error);
            return OperationResult.Fail($"Could not turn on System Protection for {root}: {error}");
        }

        if (_settings.Current.RestorePoints.BypassFrequencyLimit)
        {
            TryApplyFrequencyOverride();
        }

        _logger.LogInformation("System Protection enabled on {Drive}.", root);
        return OperationResult.Ok($"System Protection is now on for {root}. Windows will keep restore points on this drive.");
    }

    public async Task<IReadOnlyList<RestorePoint>> ListAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return [];
        }

        return await TryQueryPointsAsync(cancellationToken).ConfigureAwait(false) ?? [];
    }

    /// <summary>Lists restore points newest first; null when neither WMI nor PowerShell could answer.</summary>
    private async Task<List<RestorePoint>?> TryQueryPointsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var points = await Task.Run(QueryViaWmi, cancellationToken).WaitAsync(ListTimeout, cancellationToken).ConfigureAwait(false);
            return points.OrderByDescending(p => p.SequenceNumber).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI SystemRestore query failed; falling back to PowerShell.");
        }

        try
        {
            var result = await PowerShellRunner.RunAsync(
                "(Get-ComputerRestorePoint | Select-Object SequenceNumber, Description, CreationTime, RestorePointType) | ConvertTo-Json -Depth 4 -Compress",
                ListTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning("Get-ComputerRestorePoint failed: {Error}", FirstLine(result.StdErr) ?? $"exit code {result.ExitCode}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(result.StdOut))
            {
                return [];
            }

            var rows = PowerShellRunner.ParseJson<List<RestorePointRow>>(result.StdOut);
            if (rows is null)
            {
                _logger.LogWarning("Get-ComputerRestorePoint output could not be read.");
                return null;
            }

            return rows
                .Select(r => new RestorePoint(r.SequenceNumber, r.Description ?? string.Empty, DmtfDateTime.ParseOrDefault(r.CreationTime, DateTimeOffset.MinValue), RestorePointResultCodes.TypeName(r.RestorePointType)))
                .OrderByDescending(p => p.SequenceNumber)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Get-ComputerRestorePoint failed.");
            return null;
        }
    }

    public async Task<OperationResult<RestorePoint>> CreateAsync(string description, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult<RestorePoint>.Fail("System Restore is only available on Windows.");
        }

        description = string.IsNullOrWhiteSpace(description) ? "PC Checker restore point" : description.Trim();
        if (description.Length > MaxDescriptionLength)
        {
            description = description[..MaxDescriptionLength];
        }

        if (_settings.Current.RestorePoints.BypassFrequencyLimit)
        {
            TryApplyFrequencyOverride();
        }

        // Snapshot the newest sequence number first: CreateRestorePoint returns 0 even when the 24-hour frequency
        // limit silently suppresses creation, so success is confirmed by a new point appearing afterwards.
        var before = await TryQueryPointsAsync(cancellationToken).ConfigureAwait(false);
        long? previousNewest = before is null ? null : before.Count == 0 ? 0 : before.Max(p => p.SequenceNumber);

        uint returnValue;
        try
        {
            var text = description;
            returnValue = await Task.Run(() => InvokeCreate(text), cancellationToken).WaitAsync(CreateTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return OperationResult<RestorePoint>.Fail("Windows took more than two minutes to create the restore point");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CreateRestorePoint threw.");
            return OperationResult<RestorePoint>.Fail("Windows could not create a restore point: " + ex.Message, ex);
        }

        var (success, message) = RestorePointResultCodes.Describe(returnValue);
        if (!success)
        {
            _logger.LogWarning("CreateRestorePoint returned {Code}: {Message}", returnValue, message);
            return OperationResult<RestorePoint>.Fail(message);
        }

        var after = await TryQueryPointsAsync(cancellationToken).ConfigureAwait(false);
        if (after is null)
        {
            // Windows said it succeeded but the list cannot be read; report the point without a sequence number.
            var unverified = new RestorePoint(0, description, DateTimeOffset.Now, RestorePointResultCodes.TypeName(RestorePointResultCodes.TypeModifySettings));
            _logger.LogWarning("CreateRestorePoint succeeded but the restore point list could not be read to confirm it.");
            return OperationResult<RestorePoint>.Ok(unverified, $"Restore point \"{description}\" created (Windows did not report its details).");
        }

        var created = RestorePointLookup.FindCreated(after, description, previousNewest);
        if (created is null)
        {
            _logger.LogWarning("CreateRestorePoint returned 0 but no new restore point appeared (newest before: {Before}, after: {After}).",
                previousNewest, after.Count == 0 ? 0 : after.Max(p => p.SequenceNumber));
            return OperationResult<RestorePoint>.Fail(NoNewPointMessage);
        }

        _logger.LogInformation("Restore point {Sequence} created: {Description}", created.SequenceNumber, created.Description);
        return OperationResult<RestorePoint>.Ok(created, $"Restore point \"{created.Description}\" created.");
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static uint InvokeCreate(string description)
    {
        using var systemRestore = new ManagementClass(WmiPath);
        using var parameters = systemRestore.GetMethodParameters("CreateRestorePoint");
        parameters["Description"] = description;
        parameters["RestorePointType"] = RestorePointResultCodes.TypeModifySettings;
        parameters["EventType"] = RestorePointResultCodes.EventBeginSystemChange;
        using var result = systemRestore.InvokeMethod("CreateRestorePoint", parameters, null);
        return Convert.ToUInt32(result?["ReturnValue"] ?? 0u, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<RestorePoint> QueryViaWmi()
    {
        var list = new List<RestorePoint>();
        var scope = new ManagementScope(@"\\.\root\default");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT SequenceNumber, Description, CreationTime, RestorePointType FROM SystemRestore"));
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                long sequence = Convert.ToInt64(item["SequenceNumber"] ?? 0, System.Globalization.CultureInfo.InvariantCulture);
                var description = item["Description"]?.ToString() ?? string.Empty;
                var creation = DmtfDateTime.ParseOrDefault(item["CreationTime"]?.ToString(), DateTimeOffset.MinValue);
                long type = Convert.ToInt64(item["RestorePointType"] ?? 0, System.Globalization.CultureInfo.InvariantCulture);
                list.Add(new RestorePoint(sequence, description, creation, RestorePointResultCodes.TypeName(type)));
            }
        }

        return list;
    }

    /// <summary>True/false when the registry answers; null when the keys are missing.</summary>
    private bool? ReadEnabledFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemRestoreKey);
            if (key is null)
            {
                return null;
            }

            if (key.GetValue("DisableSR") is int disabled && disabled == 1)
            {
                return false;
            }

            if (key.GetValue("RPSessionInterval") is int interval)
            {
                return interval > 0;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the System Restore registry key.");
            return null;
        }
    }

    private void TryApplyFrequencyOverride()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(SystemRestoreKey, writable: true);
            if (key is null || key.GetValue(FrequencyValue) is int current && current == 0)
            {
                return;
            }

            key.SetValue(FrequencyValue, 0, RegistryValueKind.DWord);
            _logger.LogDebug("Set {Value}=0 so more than one restore point per day is allowed.", FrequencyValue);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not set the restore point frequency override.");
        }
    }

    private static string NormalizeDrive(string? drive)
    {
        var text = string.IsNullOrWhiteSpace(drive) ? Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\" : drive.Trim();
        if (text.Length == 1)
        {
            text += ":";
        }

        if (text.Length == 2 && text[1] == ':')
        {
            text += "\\";
        }

        return text;
    }

    private sealed class RestorePointRow
    {
        public long SequenceNumber { get; set; }
        public string? Description { get; set; }
        public string? CreationTime { get; set; }
        public long RestorePointType { get; set; }
    }
}

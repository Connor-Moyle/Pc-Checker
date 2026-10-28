using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Privacy;

/// <summary>
/// Windows implementation of <see cref="IPrivacyService"/>: reads the consent store in HKCU and HKLM on a background
/// thread and writes one Store app's <c>Value</c> through the change guard (journaled, no restore point: it is a
/// per-app privacy choice, not system configuration).
/// </summary>
public sealed class WindowsPrivacyService : IPrivacyService
{
    private const string ModuleName = "Privacy";
    private const string Repository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsPrivacyService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public WindowsPrivacyService(IChangeGuard guard, ILogger<WindowsPrivacyService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public Task<PrivacySnapshot> ReadAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var warnings = new List<string>();
        var capabilities = new List<CapabilityStatus>();
        foreach (var capability in Enum.GetValues<CapabilityKind>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                capabilities.Add(ReadCapability(capability));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                _logger.LogWarning(ex, "Could not read the {Capability} consent store.", capability);
                warnings.Add($"{ConsentStoreParser.Name(capability)} use could not be read.");
                capabilities.Add(new CapabilityStatus(capability, ConsentValue.Unknown, ConsentValue.Unknown, ConsentValue.Unknown, []));
            }
        }

        return new PrivacySnapshot(capabilities, warnings, ElevatedUserScope.Note);
    }, cancellationToken);

    public Task<OperationResult> SetAppAccessAsync(CapabilityAppUsage app, ConsentValue value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.IsPackaged)
        {
            return Task.FromResult(OperationResult.Fail("Desktop apps share one switch in Windows Settings; open the privacy page to change it."));
        }

        string capabilityKey = ConsentStoreParser.KeyName(app.Capability);
        string path = $@"{ConsentStoreParser.StorePath}\{capabilityKey}\{app.KeyName}";
        var hive = app.Scope == ConsentScope.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        string what = value == ConsentValue.Deny ? "Turn off" : "Restore";
        string capabilityName = ConsentStoreParser.Name(app.Capability).ToLowerInvariant();
        var options = new ChangeOptions(false, false, ModuleName, $@"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{path}\Value",
            Reversible: true, UndoHint: $"Set it back in Settings > Privacy & security > {ConsentStoreParser.Name(app.Capability)}, or write Value = {ConsentStoreParser.Format(app.Value == ConsentValue.Unknown ? ConsentValue.Allow : app.Value)}.");

        return _guard.RunGuardedAsync($"{what} {capabilityName} access for {app.DisplayName}", options, _ => Task.Run(() =>
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(path, writable: true);
                if (key is null)
                {
                    return OperationResult.Fail($"{app.DisplayName} no longer has a {capabilityName} record.");
                }

                var before = key.GetValue(ConsentStoreParser.ValueName) as string;
                if (value == ConsentValue.Unknown)
                {
                    key.DeleteValue(ConsentStoreParser.ValueName, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(ConsentStoreParser.ValueName, ConsentStoreParser.Format(value), RegistryValueKind.String);
                }

                string message = value == ConsentValue.Deny
                    ? $"{app.DisplayName} can no longer use the {capabilityName}."
                    : $"{app.DisplayName}'s {capabilityName} access is back to {(value == ConsentValue.Unknown ? "the Windows default" : ConsentStoreParser.Format(value))}.";
                _logger.LogInformation("Consent {Path} changed from {Before} to {After}.", path, before ?? "(none)", value);
                return ElevatedUserScope.Annotate(OperationResult.Ok(message));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return OperationResult.Fail($"The setting could not be changed: {ex.Message}", ex);
            }
        }), null, cancellationToken);
    }

    public OperationResult OpenSettings(CapabilityKind capability) => ShellLauncher.Open(ConsentStoreParser.SettingsUri(capability));

    private CapabilityStatus ReadCapability(CapabilityKind capability)
    {
        string capabilityPath = $@"{ConsentStoreParser.StorePath}\{ConsentStoreParser.KeyName(capability)}";
        var records = new List<CapabilityAppUsage>();
        var device = ConsentValue.Unknown;
        var apps = ConsentValue.Unknown;
        var desktop = ConsentValue.Unknown;

        foreach (var (hive, scope) in new[] { (RegistryHive.CurrentUser, ConsentScope.CurrentUser), (RegistryHive.LocalMachine, ConsentScope.Machine) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(capabilityPath);
            if (key is null)
            {
                continue;
            }

            var switchValue = ConsentStoreParser.ParseValue(key.GetValue(ConsentStoreParser.ValueName) as string);
            if (scope == ConsentScope.Machine)
            {
                device = switchValue;
            }
            else
            {
                apps = switchValue;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub is null)
                {
                    continue;
                }

                if (name.Equals(ConsentStoreParser.NonPackaged, StringComparison.OrdinalIgnoreCase))
                {
                    if (scope == ConsentScope.CurrentUser)
                    {
                        desktop = ConsentStoreParser.ParseValue(sub.GetValue(ConsentStoreParser.ValueName) as string);
                    }

                    foreach (var program in sub.GetSubKeyNames())
                    {
                        using var programKey = sub.OpenSubKey(program);
                        if (programKey is not null && Record(capability, program, programKey, isPackaged: false, scope) is { } desktopApp)
                        {
                            records.Add(desktopApp);
                        }
                    }
                }
                else if (Record(capability, name, sub, isPackaged: true, scope) is { } storeApp)
                {
                    records.Add(storeApp);
                }
            }
        }

        return new CapabilityStatus(capability, device, apps, desktop, ConsentStoreParser.Merge(records));
    }

    private CapabilityAppUsage? Record(CapabilityKind capability, string keyName, RegistryKey key, bool isPackaged, ConsentScope scope)
    {
        var startRaw = key.GetValue(ConsentStoreParser.StartName);
        var start = ConsentStoreParser.FromFileTime(startRaw);
        if (start is null)
        {
            return null; // Never used: not part of "recent use".
        }

        var stopRaw = key.GetValue(ConsentStoreParser.StopName);
        string? path = isPackaged ? null : ConsentStoreParser.DecodeNonPackagedPath(keyName);
        string display = isPackaged ? PackageDisplayName(keyName) : ProgramDisplayName(path!);
        return new CapabilityAppUsage(capability, keyName, display, path, isPackaged, start, ConsentStoreParser.FromFileTime(stopRaw),
            ConsentStoreParser.IsInUse(startRaw, stopRaw), ConsentStoreParser.ParseValue(key.GetValue(ConsentStoreParser.ValueName) as string), scope);
    }

    private string ProgramDisplayName(string path)
    {
        if (_names.TryGetValue(path, out var cached))
        {
            return cached;
        }

        string name = ConsentStoreParser.ProgramName(path);
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
            {
                name = description.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        _names[path] = name;
        return name;
    }

    /// <summary>The Store app's own display name from the package repository, translated by Windows; a readable fallback otherwise.</summary>
    private string PackageDisplayName(string familyName)
    {
        if (_names.TryGetValue(familyName, out var cached))
        {
            return cached;
        }

        string name = PackagedAppNames.FriendlyName(familyName);
        try
        {
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var family = classes.OpenSubKey($@"{Repository}\Families\{familyName}");
            var fullName = family?.GetSubKeyNames().OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (fullName is not null)
            {
                using var package = classes.OpenSubKey($@"{Repository}\Packages\{fullName}");
                if (package?.GetValue("DisplayName") is string raw && Resolve(raw) is { Length: > 0 } resolved)
                {
                    name = resolved;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        _names[familyName] = name;
        return name;
    }

    private static string? Resolve(string raw)
    {
        if (!raw.StartsWith('@'))
        {
            return raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : raw;
        }

        var buffer = new StringBuilder(512);
        return SHLoadIndirectString(raw, buffer, buffer.Capacity, IntPtr.Zero) == 0 ? buffer.ToString() : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string source, StringBuilder outBuffer, int outBufferSize, IntPtr reserved);
}

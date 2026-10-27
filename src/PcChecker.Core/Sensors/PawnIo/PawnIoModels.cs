using PcChecker.Core.Common;

namespace PcChecker.Core.Sensors.PawnIo;

/// <summary>Where the PawnIO driver stands on this PC.</summary>
public enum PawnIoState
{
    /// <summary>Could not be checked (registry or device access failed in an unexpected way).</summary>
    Unknown,

    /// <summary>Not a Windows PC (tests, Linux builds): PawnIO does not apply.</summary>
    NotSupported,

    /// <summary>No uninstall entry, no driver service and no device: PawnIO has never been installed.</summary>
    NotInstalled,

    /// <summary>Installed (uninstall entry or driver service present) but its device cannot be opened: not started yet, or blocked.</summary>
    InstalledNotRunning,

    /// <summary>The PawnIO device opens: LibreHardwareMonitor can read the CPU and the motherboard sensor chip.</summary>
    Running,
}

/// <summary>
/// What the PawnIO probe saw. <paramref name="Version"/> is the uninstall entry's DisplayVersion; <paramref name="ServiceRegistered"/>
/// says whether the PawnIO driver service exists; <paramref name="DeviceOpens"/> is the definitive "running" check
/// (null when the open failed for a reason that says nothing either way).
/// </summary>
public sealed record PawnIoStatus(PawnIoState State, string? Version, bool? ServiceRegistered, bool? DeviceOpens)
{
    public static PawnIoStatus NotSupported { get; } = new(PawnIoState.NotSupported, null, null, null);

    /// <summary>True when the driver is running, so the motherboard sensor chip can be read.</summary>
    public bool IsRunning => State == PawnIoState.Running;

    /// <summary>True when an "Install PawnIO" offer makes sense (not installed, or installed but not running).</summary>
    public bool NeedsAttention => State is PawnIoState.NotInstalled or PawnIoState.InstalledNotRunning;

    /// <summary>One plain-English line for pages and the diagnostic report.</summary>
    public string Summary => PawnIoRules.Describe(this);
}

/// <summary>How the one-click install went, and what the probe says afterwards.</summary>
public sealed record PawnIoInstallResult(bool Success, string Message, PawnIoStatus After, bool RebootRequired, PawnIoInstallMethod Method)
{
    public OperationResult ToOperationResult() => Success ? OperationResult.Ok(Message) : OperationResult.Fail(Message);
}

/// <summary>Which route the installer took.</summary>
public enum PawnIoInstallMethod
{
    None,
    Winget,
    OfficialSetup,
}

/// <summary>A program to start and its arguments (each one passed through <c>ProcessStartInfo.ArgumentList</c>).</summary>
public sealed record InstallCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>The command as a user would type it (for logs and the confirmation text).</summary>
    public string Display => string.Join(" ", new[] { FileName[(FileName.LastIndexOfAny(['\\', '/']) + 1)..] }
        .Concat(Arguments.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));
}

/// <summary>The PawnIO_setup.exe asset of a PawnIO.Setup GitHub release.</summary>
public sealed record PawnIoReleaseAsset(string Tag, string Name, Uri DownloadUrl, long Size);

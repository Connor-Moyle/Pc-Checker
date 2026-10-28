namespace PcChecker.Core.Crashes;

/// <summary>What kind of crash an entry on the Crashes page is.</summary>
public enum CrashKind
{
    /// <summary>Windows stopped with a stop code (a "blue screen", BugCheck).</summary>
    BlueScreen,

    /// <summary>The PC turned off or restarted without shutting down properly (Kernel-Power 41, EventLog 6008).</summary>
    SuddenShutdown,

    /// <summary>The graphics driver stopped responding and Windows restarted it (Display 4101, LiveKernelReports\WATCHDOG).</summary>
    DriverReset,

    /// <summary>A program closed because of an error (Application Error 1000).</summary>
    AppCrash,

    /// <summary>A program stopped responding and was closed (Application Hang 1002).</summary>
    AppHang,
}

/// <summary>How sure the "probably caused by" guess is.</summary>
public enum BlameConfidence
{
    /// <summary>No guess.</summary>
    None,

    /// <summary>A driver was found on the stack of the crashing thread: a reasonable guess, often right.</summary>
    Low,

    /// <summary>A stop-code parameter or the crash address points inside the driver: usually right.</summary>
    Medium,
}

/// <summary>A driver or program file loaded at the time of the crash, with the memory range it occupied.</summary>
public sealed record CrashModule(string Name, ulong Base, uint Size)
{
    public ulong End => Base + Size;

    public bool Contains(ulong address) => Size > 0 && address >= Base && address < End;
}

/// <summary>The "probably caused by" guess and why.</summary>
public sealed record CrashBlame(string Module, BlameConfidence Confidence, string Reason, bool IsWindowsModule);

/// <summary>
/// One crash, shutdown or driver reset. Values come from event data and dump headers (never from translated
/// message text). Optional fields are null when the source did not have them.
/// </summary>
public sealed record CrashEvent
{
    /// <summary>Stable id for selection and de-duplication (source + time).</summary>
    public required string Id { get; init; }

    public required CrashKind Kind { get; init; }

    public required DateTimeOffset Time { get; init; }

    /// <summary>Where it was found, in plain words ("System log, event 1001", "Minidump file").</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The event log and event id, for "Open in Event Viewer"; null for an entry that came from a dump file only.</summary>
    public string? LogName { get; init; }

    public int? EventId { get; init; }

    // Blue screens

    public uint? BugCheckCode { get; init; }

    public IReadOnlyList<ulong> Parameters { get; init; } = [];

    /// <summary>The dump file that belongs to this crash, when one was found.</summary>
    public string? DumpPath { get; init; }

    /// <summary>Windows build from the dump header (for example 26100).</summary>
    public int? OsBuild { get; init; }

    public CrashBlame? Blame { get; init; }

    /// <summary>Drivers loaded at the time (from a small memory dump); empty when unknown.</summary>
    public IReadOnlyList<CrashModule> Modules { get; init; } = [];

    /// <summary>Kernel-Power 41: the power button was held down to turn the PC off.</summary>
    public bool PowerButtonHeld { get; init; }

    // Driver resets

    /// <summary>The graphics driver named by the event (nvlddmkm, amdkmdag, igfx…).</summary>
    public string? Driver { get; init; }

    // App crashes and hangs

    public string? AppName { get; init; }

    public string? AppVersion { get; init; }

    public string? AppPath { get; init; }

    public string? ModuleName { get; init; }

    public string? ModuleVersion { get; init; }

    public uint? ExceptionCode { get; init; }

    /// <summary>Offset inside the faulting module, as Windows wrote it (hex).</summary>
    public string? FaultOffset { get; init; }

    /// <summary>Windows Error Reporting report id, when the event has one.</summary>
    public string? ReportId { get; init; }
}

/// <summary>Everything the Crashes page shows, newest first.</summary>
public sealed record CrashReport
{
    public DateTimeOffset Captured { get; init; } = DateTimeOffset.Now;

    /// <summary>How many days back the scan looked.</summary>
    public int DaysCovered { get; init; } = CrashRules.WindowDays;

    public IReadOnlyList<CrashEvent> Events { get; init; } = [];

    /// <summary>WHEA hardware error reports in the last 30 days (shown in full on the Health page); null when not read.</summary>
    public int? WheaErrors { get; init; }

    /// <summary>True when PC Checker has an overclock applied (BIOS overclocks such as XMP or PBO cannot be seen).</summary>
    public bool OverclockApplied { get; init; }

    /// <summary>Things that could not be read, in plain words (for example the dump folder needs administrator rights).</summary>
    public IReadOnlyList<CrashWarning> Warnings { get; init; } = [];
}

/// <summary>Something the scan could not read: an English format ("{0} needs administrator rights…") and its values.</summary>
public sealed record CrashWarning(string Format, params string[] Args)
{
    /// <summary>The warning in the language of <paramref name="tr"/> (English when null).</summary>
    public string Text(Localization.Translator? tr = null) => (tr ?? Localization.Translator.English).F(Format, Args.Cast<object?>().ToArray());

    public override string ToString() => Text();
}

/// <summary>A quick count for the Dashboard: blue screens and sudden shutdowns in a recent window.</summary>
public sealed record CrashGlance(int BlueScreens, int SuddenShutdowns, DateTimeOffset? Latest);

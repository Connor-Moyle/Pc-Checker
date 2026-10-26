using PcChecker.Core.Common;
using PcChecker.Core.Performance;

namespace PcChecker.Core.Startup;

/// <summary>One thing worth telling the user about startup or boot.</summary>
public sealed record StartupWarning(string Id, Severity Severity, string Title, string Detail);

/// <summary>Turns boot facts and the startup list into plain-English warnings. Pure.</summary>
public static class BootConfigRules
{
    public const int LongTimeoutSeconds = 10;
    public const double SlowBootSeconds = 60;
    public const double VerySlowBootSeconds = 120;

    public static IReadOnlyList<StartupWarning> Evaluate(BootConfig? boot, IReadOnlyList<StartupItem>? items, int manyStartupItems = 10, bool warnOnManyItems = true)
    {
        var warnings = new List<StartupWarning>();

        if (boot is not null)
        {
            if (boot.SafeModeNextBoot)
            {
                warnings.Add(new("boot.safemode", Severity.High, "Safe Mode is armed for the next restart",
                    "Windows will start in Safe Mode with only basic drivers until you turn this off again on this page (or run: bcdedit /deletevalue {current} safeboot)."));
            }

            if (boot.MemoryDiagnosticScheduled)
            {
                warnings.Add(new("boot.memdiag", Severity.Info, "A memory test will run at the next restart",
                    "Windows Memory Diagnostic is scheduled. The PC will take several extra minutes to start while it tests your RAM."));
            }

            if (boot.TimeoutSeconds >= LongTimeoutSeconds)
            {
                warnings.Add(new("boot.timeout", Severity.Low, $"The boot menu waits {boot.TimeoutSeconds} seconds",
                    "When more than one operating system is installed, Windows waits this long before starting the default one. 3 seconds is plenty."));
            }

            if (boot.LastBootSeconds is { } seconds)
            {
                if (seconds >= VerySlowBootSeconds)
                {
                    warnings.Add(new("boot.veryslow", Severity.High, $"The last startup took {seconds:0} seconds",
                        "Over two minutes is very slow. Disable heavy startup apps below and check the Storage and Health pages."));
                }
                else if (seconds >= SlowBootSeconds)
                {
                    warnings.Add(new("boot.slow", Severity.Medium, $"The last startup took {seconds:0} seconds",
                        "Fewer startup apps and fast startup usually bring this under 30 seconds on an SSD."));
                }
            }

            if (!boot.FastStartup && !boot.HibernationEnabled)
            {
                warnings.Add(new("boot.faststartup.off", Severity.Info, "Fast startup is off",
                    "Turning it on makes cold boots quicker by saving the Windows kernel to disk at shutdown. Leave it off if you dual-boot or need a true fresh start every time."));
            }
        }

        if (items is not null && warnOnManyItems)
        {
            int enabled = items.Count(i => i.Enabled && i.Kind != StartupKinds.Winlogon);
            if (enabled >= manyStartupItems)
            {
                warnings.Add(new("startup.many", enabled >= manyStartupItems * 2 ? Severity.High : Severity.Medium,
                    $"{enabled} programs start with Windows",
                    "Each one adds time to logon and uses memory in the background. Turn off anything you do not need right after sign-in; you can still open it later."));
            }

            if (items.Any(i => i.Kind == StartupKinds.Winlogon))
            {
                warnings.Add(new("startup.winlogon", Severity.Critical, "Unusual logon settings found",
                    "The Winlogon Shell or Userinit value is not the Windows default. This can be malware. See the advanced section and run a Microsoft Defender scan."));
            }
        }

        return warnings.OrderByDescending(w => w.Severity).ToList();
    }
}

/// <summary>Values used in <see cref="StartupItem.Kind"/>.</summary>
public static class StartupKinds
{
    public const string Registry = "Registry";
    public const string Folder = "Folder";
    public const string Task = "Task";

    /// <summary>Winlogon Shell/Userinit anomalies (advanced information; never toggled).</summary>
    public const string Winlogon = "Winlogon";
}

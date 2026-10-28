using PcChecker.Core.Localization;
using PcChecker.Core.Security;

namespace PcChecker.Core.Upgrades;

/// <summary>The facts Windows 11 readiness needs, read by other modules (Security, hardware inventory).</summary>
public sealed record Windows11Inputs(
    int? Build,
    TpmInfo? Tpm,
    bool? SecureBootOn,
    bool LegacyBios,
    bool? CpuSupported,
    long? MemoryBytes,
    long? SystemDriveBytes);

/// <summary>
/// Pure Windows 11 readiness rules for Windows 10 PCs: TPM 2.0, UEFI with Secure Boot capability, a supported processor
/// (Intel 8th gen or newer, AMD Ryzen 2000 / Zen+ or newer), 4 GB of memory and a 64 GB system drive. A TPM that is
/// present but switched off and Secure Boot that is off are settings in the BIOS, so they do not block the upgrade.
/// </summary>
public static class Windows11Rules
{
    /// <summary>The first Windows 11 build.</summary>
    public const int FirstWindows11Build = 22000;

    private const long Gb = 1024L * 1024 * 1024;

    public static Windows11Readiness Evaluate(Windows11Inputs inputs, Translator tr)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(tr);
        if (inputs.Build is >= FirstWindows11Build)
        {
            return new Windows11Readiness(Win11Status.AlreadyWindows11, tr.T("This PC already runs Windows 11."), []);
        }

        var checks = new List<ReadinessCheck>
        {
            TpmCheck(inputs.Tpm, tr),
            BootCheck(inputs.LegacyBios, inputs.SecureBootOn, tr),
            inputs.CpuSupported switch
            {
                true => new ReadinessCheck("cpu", true, tr.T("Processor"), tr.T("Your processor is on Microsoft's supported list.")),
                false => new ReadinessCheck("cpu", false, tr.T("Processor"),
                    tr.T("Your processor is older than Windows 11 supports (Intel 8th gen or AMD Ryzen 2000 and newer). Only a new processor, and usually a new board, fixes this.")),
                _ => new ReadinessCheck("cpu", null, tr.T("Processor"), tr.T("PC Checker could not tell whether your processor is supported. Microsoft's PC Health Check app can.")),
            },
            inputs.MemoryBytes is { } mem
                ? new ReadinessCheck("memory", mem >= 4 * Gb - 512L * 1024 * 1024, tr.T("Memory"),
                    mem >= 4 * Gb - 512L * 1024 * 1024 ? tr.T("At least 4 GB of memory.") : tr.T("Windows 11 needs at least 4 GB of memory."))
                : new ReadinessCheck("memory", null, tr.T("Memory"), tr.T("The amount of memory could not be read.")),
            inputs.SystemDriveBytes is { } disk
                ? new ReadinessCheck("storage", disk >= 64 * 1_000_000_000L, tr.T("Storage"),
                    disk >= 64 * 1_000_000_000L ? tr.T("The system drive is 64 GB or larger.") : tr.T("Windows 11 needs a system drive of at least 64 GB."))
                : new ReadinessCheck("storage", null, tr.T("Storage"), tr.T("The size of the system drive could not be read.")),
        };

        Win11Status status;
        string summary;
        if (checks.Any(c => c.Passed == false && !c.FixableInBios))
        {
            status = Win11Status.NotReady;
            summary = tr.T("This PC cannot officially run Windows 11 as it is. Windows 10 no longer gets regular security updates, so plan the upgrade below.");
        }
        else if (checks.Any(c => c.Passed == false))
        {
            status = Win11Status.ReadyAfterBiosChange;
            summary = tr.T("This PC can run Windows 11 after a free change in the BIOS (see Fix first). Then upgrade through Windows Update.");
        }
        else if (checks.Any(c => c.Passed is null))
        {
            status = Win11Status.Unknown;
            summary = tr.T("Some requirements could not be checked. Microsoft's PC Health Check app gives the final answer.");
        }
        else
        {
            status = Win11Status.Ready;
            summary = tr.T("This PC meets the Windows 11 requirements. Windows 10 no longer gets regular security updates, so upgrade for free through Windows Update.");
        }

        return new Windows11Readiness(status, summary, checks);
    }

    private static ReadinessCheck TpmCheck(TpmInfo? tpm, Translator tr)
    {
        if (tpm is null)
        {
            return new ReadinessCheck("tpm", null, tr.T("TPM 2.0"), tr.T("The security chip (TPM) could not be read."));
        }

        if (!tpm.Present)
        {
            return new ReadinessCheck("tpm", false, tr.T("TPM 2.0"),
                tr.T("No TPM is visible. Most PCs from 2017 on have one built into the processor, switched off in the BIOS: turn on \"fTPM\" (AMD) or \"PTT\" (Intel)."),
                FixableInBios: true);
        }

        bool v2 = (tpm.SpecVersion ?? string.Empty).TrimStart().StartsWith('2');
        if (tpm.SpecVersion is not null && !v2)
        {
            return new ReadinessCheck("tpm", false, tr.T("TPM 2.0"),
                tr.T("The TPM is version 1.2. Windows 11 needs 2.0; some boards can switch to the processor's own TPM 2.0 in the BIOS."), FixableInBios: true);
        }

        if (tpm.Enabled == false || tpm.Activated == false)
        {
            return new ReadinessCheck("tpm", false, tr.T("TPM 2.0"),
                tr.T("A TPM is present but switched off. Turn it on in the BIOS (\"fTPM\", \"PTT\" or \"Security Device Support\")."), FixableInBios: true);
        }

        return new ReadinessCheck("tpm", tpm.SpecVersion is null ? null : true, tr.T("TPM 2.0"),
            tpm.SpecVersion is null ? tr.T("A TPM is present; its version could not be read.") : tr.T("TPM 2.0 is present and on."));
    }

    private static ReadinessCheck BootCheck(bool legacyBios, bool? secureBootOn, Translator tr)
    {
        if (legacyBios)
        {
            return new ReadinessCheck("secureboot", false, tr.T("UEFI and Secure Boot"),
                tr.T("The PC starts in legacy (CSM) mode. Converting the disk with Windows' MBR2GPT tool and switching the BIOS to UEFI fixes it; back up first."),
                FixableInBios: true);
        }

        return secureBootOn switch
        {
            true => new ReadinessCheck("secureboot", true, tr.T("UEFI and Secure Boot"), tr.T("The PC starts in UEFI mode with Secure Boot on.")),
            false => new ReadinessCheck("secureboot", true, tr.T("UEFI and Secure Boot"),
                tr.T("UEFI mode, so Secure Boot is available. It is off now; turning it on in the BIOS is recommended but not needed to upgrade.")),
            _ => new ReadinessCheck("secureboot", null, tr.T("UEFI and Secure Boot"), tr.T("The start-up mode could not be read.")),
        };
    }
}

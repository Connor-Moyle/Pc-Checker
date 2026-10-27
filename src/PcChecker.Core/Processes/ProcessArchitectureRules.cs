namespace PcChecker.Core.Processes;

/// <summary>Maps Windows machine types (IMAGE_FILE_MACHINE_*) to <see cref="ProcessArchitecture"/>.</summary>
public static class ProcessArchitectureRules
{
    public const ushort MachineUnknown = 0;
    public const ushort MachineI386 = 0x014C;
    public const ushort MachineArmNt = 0x01C4;
    public const ushort MachineAmd64 = 0x8664;
    public const ushort MachineArm64 = 0xAA64;

    /// <summary>The architecture of one machine type.</summary>
    public static ProcessArchitecture FromMachine(ushort machine) => machine switch
    {
        MachineI386 => ProcessArchitecture.X86,
        MachineAmd64 => ProcessArchitecture.X64,
        MachineArm64 => ProcessArchitecture.Arm64,
        MachineArmNt => ProcessArchitecture.Arm,
        _ => ProcessArchitecture.Unknown,
    };

    /// <summary>
    /// From IsWow64Process2: a process that does not run under WOW64 reports "unknown" and has the PC's own
    /// architecture; otherwise its own machine type counts.
    /// </summary>
    public static ProcessArchitecture FromWow64(ushort processMachine, ushort nativeMachine) =>
        processMachine == MachineUnknown ? FromMachine(nativeMachine) : FromMachine(processMachine);

    /// <summary>"x64", "x86", "ARM64", "ARM" or an empty string.</summary>
    public static string Label(ProcessArchitecture architecture) => architecture switch
    {
        ProcessArchitecture.X64 => "x64",
        ProcessArchitecture.X86 => "x86",
        ProcessArchitecture.Arm64 => "ARM64",
        ProcessArchitecture.Arm => "ARM",
        _ => string.Empty,
    };
}

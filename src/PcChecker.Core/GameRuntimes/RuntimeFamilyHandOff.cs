namespace PcChecker.Core.GameRuntimes;

/// <summary>Asks the Missing DLLs page to show one runtime family (vc14, directx, physx…), sent from the Crashes page.</summary>
public sealed record RuntimeFamilyRequest(string FamilyId);

/// <summary>Crashes page → Missing DLLs page: preselect the runtime package a crash points at.</summary>
public sealed class RuntimeFamilyHandOff : Games.GameHandOff<RuntimeFamilyRequest>;

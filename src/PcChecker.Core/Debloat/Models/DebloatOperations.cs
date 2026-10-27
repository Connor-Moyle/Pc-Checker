namespace PcChecker.Core.Debloat;

/// <summary>Registry root used by a debloat operation.</summary>
public enum DebloatHive
{
    CurrentUser,
    LocalMachine,

    /// <summary>HKEY_USERS (only used for <c>.DEFAULT</c>, the profile of the sign-in screen).</summary>
    Users,
}

/// <summary>Registry value types the catalog uses.</summary>
public enum DebloatValueKind
{
    DWord,
    String,
    Binary,

    /// <summary>REG_EXPAND_SZ (only ever captured, so a revert writes back the exact type it found).</summary>
    ExpandString,

    /// <summary>REG_MULTI_SZ, stored as lines separated by "\n" (captured only).</summary>
    MultiString,

    /// <summary>REG_QWORD, stored as decimal text (captured only).</summary>
    QWord,
}

/// <summary>Windows service start types (as shown in services.msc).</summary>
public enum DebloatStartMode
{
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
    Boot,
    System,
}

/// <summary>One change inside a tweak. <see cref="Key"/> identifies it in the capture file.</summary>
public abstract record DebloatOperation
{
    /// <summary>Stable identity used for captured "before" values.</summary>
    public abstract string Key { get; }

    /// <summary>False for one-way operations (app removal, uninstall scripts without a revert).</summary>
    public virtual bool Reversible => true;

    /// <summary>True when the work is done by another PC Checker service (Windows Features) that guards it itself.</summary>
    public virtual bool IsExternal => false;

    /// <summary>One plain line for confirmation dialogs and docs.</summary>
    public abstract string Describe();
}

/// <summary>
/// Sets a registry value. <see cref="AppliedValue"/> null means "delete the value when applied"; <see cref="DefaultValue"/>
/// null means "the value is absent by default", so a revert without a captured value deletes it. Values use the
/// canonical text of <see cref="RegistryValueCodec"/> (DWORD decimal, string as-is, binary as hex).
/// </summary>
public sealed record RegistryValueOp(
    DebloatHive Hive,
    string Path,
    string Name,
    DebloatValueKind Kind,
    string? AppliedValue,
    string? DefaultValue) : DebloatOperation
{
    /// <summary>
    /// A key this operation may create. Revert deletes it (with everything under it) when it did not exist before the
    /// change, or when nothing was captured, so no empty override key is left behind (classic context menu, Home/Gallery).
    /// </summary>
    public string? DeleteKeyOnRevert { get; init; }

    public override string Key => $"reg:{RegistryValueCodec.HiveName(Hive)}\\{Path}\\{Name}";

    public override string Describe()
    {
        var where = $"{RegistryValueCodec.HiveName(Hive)}\\{Path}\\{(Name.Length == 0 ? "(Default)" : Name)}";
        return AppliedValue is null ? $"Remove {where}" : $"Set {where} = {RegistryValueCodec.Display(Kind, AppliedValue)}";
    }
}

/// <summary>
/// Deletes a whole registry key (for example a File Explorer namespace entry). The key must hold nothing but its
/// default value, which <see cref="RestoredDefaultValue"/> puts back on revert, so revert recreates it exactly. When
/// the key did not exist before the change, revert leaves it absent.
/// </summary>
public sealed record RegistryKeyRemovalOp(DebloatHive Hive, string Path, string RestoredDefaultValue) : DebloatOperation
{
    public override string Key => $"regkey:{RegistryValueCodec.HiveName(Hive)}\\{Path}";

    public override string Describe() => $"Delete the key {RegistryValueCodec.HiveName(Hive)}\\{Path}";
}

/// <summary>Changes a service's start type. Revert restores the captured type, or <see cref="DefaultMode"/> when none was captured.</summary>
public sealed record ServiceOp(string Name, DebloatStartMode AppliedMode, DebloatStartMode DefaultMode) : DebloatOperation
{
    public override string Key => "svc:" + Name;

    public override string Describe() => $"Set service {Name} to {ServiceStartModes.Display(AppliedMode)}";
}

/// <summary>Disables (or enables) a scheduled task by its full path, e.g. <c>\Microsoft\Windows\Autochk\Proxy</c>.</summary>
public sealed record ScheduledTaskOp(string Path, bool DisableWhenApplied = true) : DebloatOperation
{
    public override string Key => "task:" + Path;

    public override string Describe() => $"{(DisableWhenApplied ? "Disable" : "Enable")} scheduled task {Path}";
}

/// <summary>Removes a Store app through the Windows Features service. One-way: reinstall from the Microsoft Store.</summary>
public sealed record AppxRemovalOp(string PackageName, string FriendlyName) : DebloatOperation
{
    public override string Key => "appx:" + PackageName;
    public override bool Reversible => false;
    public override bool IsExternal => true;

    public override string Describe() => $"Remove the {FriendlyName} app ({PackageName})";
}

/// <summary>Turns a Windows optional feature off (or on) through the Windows Features service.</summary>
public sealed record OptionalFeatureOp(string FeatureName, string FriendlyName, bool DisableWhenApplied = true) : DebloatOperation
{
    public override string Key => "feature:" + FeatureName;
    public override bool IsExternal => true;

    public override string Describe() => $"{(DisableWhenApplied ? "Turn off" : "Turn on")} the Windows feature {FriendlyName} ({FeatureName})";
}

/// <summary>
/// A reviewed PowerShell script for changes without a registry equivalent. <see cref="DetectScript"/> must print True
/// when the tweak is applied. A null <see cref="RevertScript"/> makes the operation one-way; <see cref="UndoHint"/> says
/// how to undo it by hand.
/// </summary>
public sealed record PowerShellOp(string Id, string Description, string ApplyScript, string? RevertScript, string DetectScript, string? UndoHint) : DebloatOperation
{
    public override string Key => "ps:" + Id;
    public override bool Reversible => RevertScript is not null;

    public override string Describe() => Description;
}

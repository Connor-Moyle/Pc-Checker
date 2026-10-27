namespace PcChecker.Core.ContextMenus;

/// <summary>What turning a shell extension off or on needs from the Blocked lists.</summary>
public enum BlockListAction
{
    /// <summary>Nothing to do: it is already in the wanted state.</summary>
    None,

    /// <summary>Add the CLSID to the current user's list (turn off).</summary>
    AddForUser,

    /// <summary>Remove the CLSID from the lists that hold it (turn on).</summary>
    Remove,
}

/// <summary>The plan for one change, with the lists that hold the CLSID now.</summary>
public sealed record BlockListPlan(BlockListAction Action, bool InUserList, bool InMachineList);

/// <summary>
/// The "Shell Extensions\Blocked" lists (HKCU and HKLM <c>Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked</c>):
/// a value named after a CLSID stops File Explorer from loading that extension. Turning off adds the CLSID for the
/// current user only; turning on removes it from every list that holds it, so the entry really comes back.
/// </summary>
public static class ShellExtensionBlockList
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    /// <summary>The value data PC Checker writes, so a later look in the registry shows who added it.</summary>
    public const string ValueData = "Turned off by PC Checker";

    /// <summary>"{guid}" in upper case with braces, or null when <paramref name="text"/> is not a GUID.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        return Guid.TryParseExact(trimmed, "B", out var braced) ? Format(braced)
            : Guid.TryParseExact(trimmed, "D", out var plain) ? Format(plain)
            : null;
    }

    /// <summary>True when either list names the CLSID (value names are compared as GUIDs, braces and case ignored).</summary>
    public static bool IsBlocked(string? clsid, IEnumerable<string> userValueNames, IEnumerable<string> machineValueNames) =>
        Contains(userValueNames, clsid) || Contains(machineValueNames, clsid);

    public static bool Contains(IEnumerable<string> valueNames, string? clsid)
    {
        var wanted = Normalize(clsid);
        return wanted is not null && valueNames.Any(n => Normalize(n) == wanted);
    }

    /// <summary>The value name in a list that matches the CLSID (lists can hold lower-case names), or null.</summary>
    public static string? MatchingName(IEnumerable<string> valueNames, string? clsid)
    {
        var wanted = Normalize(clsid);
        return wanted is null ? null : valueNames.FirstOrDefault(n => Normalize(n) == wanted);
    }

    /// <summary>What to do to turn the extension off (<paramref name="turnOn"/> false) or on.</summary>
    public static BlockListPlan Plan(string clsid, bool turnOn, IEnumerable<string> userValueNames, IEnumerable<string> machineValueNames)
    {
        bool inUser = Contains(userValueNames, clsid);
        bool inMachine = Contains(machineValueNames, clsid);
        if (turnOn)
        {
            return new BlockListPlan(inUser || inMachine ? BlockListAction.Remove : BlockListAction.None, inUser, inMachine);
        }

        return new BlockListPlan(inUser || inMachine ? BlockListAction.None : BlockListAction.AddForUser, inUser, inMachine);
    }

    private static string Format(Guid guid) => guid.ToString("B").ToUpperInvariant();
}

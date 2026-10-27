namespace PcChecker.Core.ContextMenus;

/// <summary>
/// The well-known Windows 11 switch for the full (classic) right-click menu: an empty default value under
/// <c>HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32</c> stops File Explorer from
/// loading the new compact menu, so "Show more options" is no longer needed. Per user; deleting the key undoes it.
/// </summary>
public static class ClassicMenuSwitch
{
    public const string Clsid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    /// <summary>The key under HKCU that PC Checker creates (and deletes to switch back).</summary>
    public const string KeyPath = @"Software\Classes\CLSID\" + Clsid;

    public const string InprocPath = KeyPath + @"\InprocServer32";

    /// <summary>Windows 11 starts at build 22000; older builds always have the classic menu.</summary>
    public static bool IsSupported(int windowsBuild) => windowsBuild >= 22000;

    /// <summary>
    /// True when the switch is on: the InprocServer32 key exists and its default value is present and empty.
    /// <paramref name="defaultValue"/> is null when the key or value does not exist.
    /// </summary>
    public static bool IsClassic(bool inprocKeyExists, string? defaultValue) =>
        inprocKeyExists && defaultValue is not null && defaultValue.Length == 0;

    /// <summary>True when the key existed before PC Checker touched it with content other than the switch (leave it alone).</summary>
    public static bool IsForeignRegistration(bool inprocKeyExists, string? defaultValue) =>
        inprocKeyExists && !string.IsNullOrEmpty(defaultValue);
}

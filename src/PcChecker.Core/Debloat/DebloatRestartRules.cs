namespace PcChecker.Core.Debloat;

/// <summary>What has to happen before a change made by a tweak shows.</summary>
public enum DebloatVisibility
{
    /// <summary>Shows right away (or the next time the setting is used).</summary>
    Now,

    /// <summary>File Explorer (taskbar, Start, File Explorer windows, right-click menu) reads it when it starts.</summary>
    RestartExplorer,

    /// <summary>Windows reads it when you sign in (shell and AI policies for the account).</summary>
    SignOut,

    /// <summary>Needs a Windows restart: optional features, and services that keep running until then.</summary>
    RestartWindows,
}

/// <summary>
/// Pure rules for when a tweak's change becomes visible. Windows reads most shell settings only when File Explorer
/// starts, user policies when you sign in, and a disabled service keeps running until the next restart; without
/// saying so, a tweak that was written correctly looks as if it did nothing.
/// </summary>
public static class DebloatRestartRules
{
    /// <summary>HKCU keys File Explorer reads when it starts (taskbar, Start, Explorer view, shell namespace).</summary>
    private static readonly string[] ShellUserKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Explorer",
        @"Software\Microsoft\Windows\CurrentVersion\Search",
        @"Software\Microsoft\Windows\CurrentVersion\Feeds",
        @"Software\Classes\CLSID",
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell",
    ];

    /// <summary>Policy keys the Windows shell and its AI features read when you sign in.</summary>
    private static readonly string[] ShellPolicyKeys =
    [
        @"Policies\Microsoft\Windows\WindowsCopilot",
        @"Policies\Microsoft\Windows\WindowsAI",
        @"Policies\Microsoft\Windows\Explorer",
        @"Policies\Microsoft\Windows\Windows Search",
        @"Policies\Microsoft\Windows\CloudContent",
        @"Policies\Microsoft\Dsh",
    ];

    /// <summary>Categories whose changes you see on screen (privacy and update settings have nothing to "show").</summary>
    private static readonly HashSet<string> VisibleCategories = new(StringComparer.Ordinal)
    {
        DebloatCatalog.AdsCategory, DebloatCatalog.AiCategory, DebloatCatalog.TaskbarCategory, DebloatCatalog.ExplorerCategory,
    };

    /// <summary>
    /// The strongest step the tweak needs: a Windows restart (the catalog says so, an optional feature, or a service set
    /// to Disabled, which keeps running until the restart), then an Explorer restart when the catalog marks it, then a
    /// sign-out for shell and AI policies, then an Explorer restart for other shell settings.
    /// </summary>
    public static DebloatVisibility For(DebloatTweak tweak)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        if (tweak.RequiresRestart || tweak.Operations.Any(o => o is OptionalFeatureOp || o is ServiceOp { AppliedMode: DebloatStartMode.Disabled }))
        {
            return DebloatVisibility.RestartWindows;
        }

        if (tweak.RestartsExplorer)
        {
            return DebloatVisibility.RestartExplorer;
        }

        var registry = tweak.Operations.OfType<RegistryValueOp>().ToList();
        if (VisibleCategories.Contains(tweak.Category) && registry.Any(IsShellPolicy))
        {
            return DebloatVisibility.SignOut;
        }

        return registry.Any(IsShellSetting) ? DebloatVisibility.RestartExplorer : DebloatVisibility.Now;
    }

    /// <summary>True for a policy value the shell reads at sign-in (Copilot, Recall, Explorer, search, Widgets).</summary>
    public static bool IsShellPolicy(RegistryValueOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return ShellPolicyKeys.Any(k => op.Path.Contains(k, StringComparison.OrdinalIgnoreCase))
               || (op.Hive == DebloatHive.CurrentUser && op.Path.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for a per-user shell setting that File Explorer reads when it starts.</summary>
    public static bool IsShellSetting(RegistryValueOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op.Hive == DebloatHive.CurrentUser
               && ShellUserKeys.Any(k => op.Path.Equals(k, StringComparison.OrdinalIgnoreCase) || op.Path.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The short next step shown under a result ("Restart File Explorer to see it."), or null.</summary>
    public static string? NextStep(DebloatVisibility visibility) => visibility switch
    {
        DebloatVisibility.RestartExplorer => "Restart File Explorer to see it.",
        DebloatVisibility.SignOut => "Sign out and back in to see it.",
        DebloatVisibility.RestartWindows => "Restart Windows to finish.",
        _ => null,
    };
}

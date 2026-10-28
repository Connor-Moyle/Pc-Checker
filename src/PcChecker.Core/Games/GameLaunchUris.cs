namespace PcChecker.Core.Games;

/// <summary>
/// Builds the launcher links the page opens. Every id is checked or escaped first, so text from a launcher's files can
/// never turn a link into something else (an extra argument, another scheme or a file path).
/// </summary>
public static class GameLaunchUris
{
    /// <summary>steam://rungameid/&lt;appid&gt;: Steam starts the game (and updates it first if needed).</summary>
    public static string? SteamRun(string appId) => SteamLibraryRules.IsAppId(appId) ? "steam://rungameid/" + appId : null;

    /// <summary>steam://validate/&lt;appid&gt;: Steam checks the game's files and downloads any that are missing or damaged.</summary>
    public static string? SteamValidate(string appId) => SteamLibraryRules.IsAppId(appId) ? "steam://validate/" + appId : null;

    /// <summary>steam://uninstall/&lt;appid&gt;: Steam asks the user whether to remove the game.</summary>
    public static string? SteamUninstall(string appId) => SteamLibraryRules.IsAppId(appId) ? "steam://uninstall/" + appId : null;

    /// <summary>
    /// com.epicgames.launcher://apps/&lt;namespace&gt;%3A&lt;catalogItemId&gt;%3A&lt;appName&gt;?action=&lt;action&gt;. Each part is
    /// percent-escaped; the colons between them are written as %3A as the Epic launcher expects.
    /// </summary>
    public static string? Epic(string? catalogNamespace, string? catalogItemId, string? appName, string action)
    {
        if (string.IsNullOrWhiteSpace(appName) || action is not ("launch" or "verify"))
        {
            return null;
        }

        var id = string.IsNullOrWhiteSpace(catalogNamespace) || string.IsNullOrWhiteSpace(catalogItemId)
            ? Uri.EscapeDataString(appName.Trim())
            : Uri.EscapeDataString(catalogNamespace.Trim()) + "%3A" + Uri.EscapeDataString(catalogItemId.Trim()) + "%3A" + Uri.EscapeDataString(appName.Trim());
        return "com.epicgames.launcher://apps/" + id + "?action=" + action + (action == "launch" ? "&silent=true" : string.Empty);
    }

    /// <summary>The Epic Games Launcher's own window (for actions it has no link for, such as uninstalling).</summary>
    public const string EpicLauncher = "com.epicgames.launcher://store";

    /// <summary>goggalaxy://openGameView/&lt;id&gt;: GOG Galaxy opens the game's page (play, verify, uninstall live there).</summary>
    public static string? GogGameView(string? gameId) => IsDigits(gameId) ? "goggalaxy://openGameView/" + gameId!.Trim() : null;

    /// <summary>uplay://launch/&lt;id&gt;/0: Ubisoft Connect starts the game.</summary>
    public static string? UbisoftLaunch(string? installId) => IsDigits(installId) ? "uplay://launch/" + installId!.Trim() + "/0" : null;

    /// <summary>The Ubisoft Connect window.</summary>
    public const string UbisoftLauncher = "uplay://";

    /// <summary>battlenet://&lt;code&gt;: the Battle.net app opens that game's page. Codes are letters and digits only.</summary>
    public static string? BattleNet(string? productCode) =>
        !string.IsNullOrWhiteSpace(productCode) && productCode.Length <= 16 && productCode.All(char.IsAsciiLetterOrDigit)
            ? "battlenet://" + productCode
            : null;

    /// <summary>shell:AppsFolder\&lt;AUMID&gt;: Windows starts the Store or Xbox app as the signed-in user.</summary>
    public static string? XboxApp(string? packageFamilyName, string? appId)
    {
        if (!XboxGameRules.IsPackageFamilyName(packageFamilyName) || string.IsNullOrWhiteSpace(appId)
            || !appId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            return null;
        }

        return @"shell:AppsFolder\" + packageFamilyName + "!" + appId;
    }

    /// <summary>Windows Settings' list of installed apps, where Store and Xbox games are repaired, reset or removed.</summary>
    public const string InstalledAppsSettings = "ms-settings:appsfeatures";

    private static bool IsDigits(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= 20 && text.Trim().All(char.IsAsciiDigit);
}

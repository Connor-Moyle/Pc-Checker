using PcChecker.Core.Optimization;
using static PcChecker.Core.Debloat.DebloatSources;

namespace PcChecker.Core.Debloat;

public static partial class DebloatCatalog
{
    /// <summary>
    /// Consumer apps that are safe to remove (the "Remove consumer apps" preset ticks the ones marked preselected).
    /// Removal goes through the Windows Features service; apps come back from the Microsoft Store.
    /// </summary>
    private static (string Id, string Package, string Name, bool Preselected, string Note)[] ConsumerApps =>
    [
        ("news", "Microsoft.BingNews", "News", true, "Safe to remove."),
        ("weather", "Microsoft.BingWeather", "Weather", true, "Safe to remove; weather stays available in Widgets."),
        ("money", "Microsoft.BingFinance", "Money", true, "Safe to remove."),
        ("sports", "Microsoft.BingSports", "Sports", true, "Safe to remove."),
        ("bingsearch", "Microsoft.BingSearch", "Bing Search", true, "Safe to remove; Start search becomes local only."),
        ("solitaire", "Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", true, "Safe to remove."),
        ("gethelp", "Microsoft.GetHelp", "Get Help", true, "Safe to remove; support is still available on the web."),
        ("tips", "Microsoft.Getstarted", "Tips", true, "Safe to remove."),
        ("feedbackhub", "Microsoft.WindowsFeedbackHub", "Feedback Hub", true, "Safe to remove."),
        ("people", "Microsoft.People", "People", true, "Safe to remove; contacts stay in your account."),
        ("maps", "Microsoft.WindowsMaps", "Maps", true, "Safe to remove."),
        ("teams", "MicrosoftTeams", "Microsoft Teams (personal)", true, "Safe to remove; Teams for work is a separate install."),
        ("msteams", "MSTeams", "Microsoft Teams (new)", false, "Remove only if you don't use Teams for work or school."),
        ("clipchamp", "Clipchamp.Clipchamp", "Clipchamp", true, "Safe to remove."),
        ("officehub", "Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office hub)", true, "Safe to remove; installed Office apps are not affected."),
        ("skype", "Microsoft.SkypeApp", "Skype", true, "Safe to remove."),
        ("powerautomate", "Microsoft.PowerAutomateDesktop", "Power Automate", true, "Safe to remove unless you build desktop automations."),
        ("mixedreality", "Microsoft.MixedReality.Portal", "Mixed Reality Portal", true, "Safe to remove unless you own a Mixed Reality headset."),
        ("moviestv", "Microsoft.ZuneVideo", "Movies & TV", true, "Safe to remove; purchases stay in your account."),
        ("cortana", "Microsoft.549981C3F5F10", "Cortana", true, "Safe to remove."),
        ("todo", "Microsoft.Todos", "Microsoft To Do", false, "Safe to remove; tasks stay in your account."),
        ("phonelink", "Microsoft.YourPhone", "Phone Link", false, "Remove only if you don't link a phone to this PC."),
        ("outlook", "Microsoft.OutlookForWindows", "Outlook (new)", false, "Remove only if you use another mail app."),
        ("linkedin", "7EE7776C.LinkedInforWindows", "LinkedIn", true, "Pre-installed by some PC makers. Safe to remove."),
        ("spotify", "SpotifyAB.SpotifyMusic", "Spotify (pre-installed)", true, "Pre-installed by some PC makers. Reinstall from the Store if you use it."),
        ("disney", "Disney.37853FC22B2CE", "Disney+", true, "Pre-installed by some PC makers. Safe to remove."),
        ("tiktok", "BytedancePte.Ltd.TikTok", "TikTok", true, "Pre-installed by some PC makers. Safe to remove."),
        ("instagram", "Facebook.InstagramBeta", "Instagram", true, "Pre-installed by some PC makers. Safe to remove."),
        ("primevideo", "AmazonVideo.PrimeVideo", "Prime Video", true, "Pre-installed by some PC makers. Safe to remove."),
        ("netflix", "4DF9E0F8.Netflix", "Netflix", true, "Pre-installed by some PC makers. Safe to remove."),
        ("candycrush", "king.com.CandyCrushSaga", "Candy Crush Saga", true, "Pre-installed game. Safe to remove."),
        ("candycrushsoda", "king.com.CandyCrushSodaSaga", "Candy Crush Soda Saga", true, "Pre-installed game. Safe to remove."),
    ];

    /// <summary>Useful built-in apps: listed so they can be removed on purpose, never preselected by a preset.</summary>
    private static (string Id, string Package, string Name, string Warning)[] KeepByDefaultApps =>
    [
        ("camera", "Microsoft.WindowsCamera", "Camera", "Removing it leaves no built-in camera app."),
        ("calculator", "Microsoft.WindowsCalculator", "Calculator", "Other apps and the calculator key open it."),
        ("photos", "Microsoft.Windows.Photos", "Photos", "Photos is the default picture viewer; removing it leaves no built-in viewer."),
        ("paint", "Microsoft.Paint", "Paint", "Removing it leaves no built-in image editor."),
        ("notepad", "Microsoft.WindowsNotepad", "Notepad", "Many text files open with Notepad by default."),
        ("snippingtool", "Microsoft.ScreenSketch", "Snipping Tool", "Removing it breaks the Win+Shift+S and Print Screen screenshot shortcut."),
        ("terminal", "Microsoft.WindowsTerminal", "Windows Terminal", "The default console on Windows 11; keep it unless you have a replacement."),
        ("xbox", "Microsoft.GamingApp", "Xbox app", "Needed for PC Game Pass and installing Xbox games."),
        ("gamebar", "Microsoft.XboxGamingOverlay", "Xbox Game Bar", "Removing it disables the Win+G overlay, screen recording and some game features."),
    ];

    /// <summary>Ids of the consumer apps the "Remove consumer apps" preset ticks.</summary>
    public static IReadOnlyList<string> PreselectedAppIds { get; } = ConsumerApps.Where(a => a.Preselected).Select(a => "apps." + a.Id).ToList();

    private static IEnumerable<DebloatTweak> AppTweaks()
    {
        string[] tools = From(WinUtil, Win11Debloat, Sophia, BloatyNosy, DebloatWindows10, Optimizer);
        foreach (var app in ConsumerApps)
        {
            yield return Tweak("apps." + app.Id, AppsCategory, "Remove " + app.Name,
                $"{app.Note} Reinstall it from the Microsoft Store any time.", RiskLevel.Safe, tools,
                [new AppxRemovalOp(app.Package, app.Name)]);
        }

        foreach (var app in KeepByDefaultApps)
        {
            yield return Tweak("apps." + app.Id, AppsCategory, "Remove " + app.Name,
                "A useful built-in app. Remove it only on purpose; reinstall it from the Microsoft Store.", RiskLevel.Moderate, tools,
                [new AppxRemovalOp(app.Package, app.Name)], warning: app.Warning);
        }
    }
}

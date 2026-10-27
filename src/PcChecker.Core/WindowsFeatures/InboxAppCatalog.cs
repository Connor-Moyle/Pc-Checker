using PcChecker.Core.Optimization;

namespace PcChecker.Core.WindowsFeatures;

/// <summary>Known inbox (pre-installed) Store apps with friendly names and removal advice.</summary>
public static class InboxAppCatalog
{
    public sealed record Entry(string PackageFamilyPrefix, string FriendlyName, RiskLevel Risk, string Note, bool AdvancedOnly);

    public static IReadOnlyList<Entry> All { get; } =
    [
        new("Microsoft.BingWeather", "Weather", RiskLevel.Safe, "Safe to remove. Weather is also available in the taskbar widgets.", false),
        new("Microsoft.BingNews", "News", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.BingFinance", "Money", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.BingSports", "Sports", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.GetHelp", "Get Help", RiskLevel.Safe, "Safe to remove. Microsoft support is still available on the web.", false),
        new("Microsoft.Getstarted", "Tips", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.MicrosoftOfficeHub", "Office hub", RiskLevel.Safe, "Safe to remove. Does not affect an installed Office.", false),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.MicrosoftStickyNotes", "Sticky Notes", RiskLevel.Safe, "Safe to remove if you do not use it; your notes are kept in your Microsoft account.", false),
        new("Microsoft.MixedReality.Portal", "Mixed Reality Portal", RiskLevel.Safe, "Safe to remove unless you own a Windows Mixed Reality headset.", false),
        new("Microsoft.Office.OneNote", "OneNote (Store)", RiskLevel.Safe, "Safe to remove. Does not affect OneNote from Office.", false),
        new("Microsoft.People", "People", RiskLevel.Safe, "Safe to remove. Contacts stay in your account.", false),
        new("Microsoft.SkypeApp", "Skype", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.Todos", "Microsoft To Do", RiskLevel.Safe, "Safe to remove; tasks stay in your account.", false),
        new("Microsoft.WindowsAlarms", "Clock (Alarms & Clock)", RiskLevel.Safe, "Safe to remove. Includes timers and focus sessions.", false),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.WindowsMaps", "Maps", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.WindowsSoundRecorder", "Sound Recorder", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.YourPhone", "Phone Link", RiskLevel.Safe, "Safe to remove unless you link an Android or iPhone.", false),
        new("Microsoft.ZuneMusic", "Media Player (Groove Music)", RiskLevel.Safe, "Safe to remove; use another media player.", false),
        new("Microsoft.ZuneVideo", "Movies & TV", RiskLevel.Safe, "Safe to remove; video purchases remain in your account.", false),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", RiskLevel.Safe, "Safe to remove unless you build desktop automations.", false),
        new("Microsoft.MicrosoftJournal", "Journal", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.Whiteboard", "Whiteboard", RiskLevel.Safe, "Safe to remove.", false),
        new("Clipchamp.Clipchamp", "Clipchamp", RiskLevel.Safe, "Safe to remove.", false),
        new("MicrosoftTeams", "Microsoft Teams (personal)", RiskLevel.Safe, "Safe to remove. Teams for work is a separate install.", false),
        new("MSTeams", "Microsoft Teams", RiskLevel.Safe, "Safe to remove; it can be reinstalled from the Store.", false),
        new("Microsoft.OutlookForWindows", "Outlook (new)", RiskLevel.Safe, "Safe to remove; reinstall from the Store if needed.", false),
        new("Microsoft.Windows.DevHome", "Dev Home", RiskLevel.Safe, "Safe to remove unless you use its developer dashboard.", false),
        new("Microsoft.549981C3F5F10", "Cortana", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.WindowsCamera", "Camera", RiskLevel.Moderate, "Removing it leaves no built-in camera app.", false),
        new("Microsoft.WindowsCalculator", "Calculator", RiskLevel.Moderate, "Handy to keep; other apps may open it.", false),
        new("Microsoft.ScreenSketch", "Snipping Tool", RiskLevel.Moderate, "Removing it disables the Win+Shift+S screenshot shortcut.", false),
        new("Microsoft.Paint", "Paint", RiskLevel.Moderate, "Removing it leaves no built-in image editor.", false),
        new("Microsoft.WindowsNotepad", "Notepad", RiskLevel.Moderate, "Many text files open with Notepad by default.", false),
        new("Microsoft.Windows.Photos", "Photos", RiskLevel.Moderate, "Photos is the default picture viewer; removing it leaves no built-in viewer.", false),
        new("Microsoft.WindowsTerminal", "Windows Terminal", RiskLevel.Moderate, "The default console host on Windows 11; keep unless you have a replacement.", false),
        new("Microsoft.XboxApp", "Xbox Console Companion", RiskLevel.Moderate, "Removing it can break the Game Bar and Xbox sign-in in games.", false),
        new("Microsoft.GamingApp", "Xbox app", RiskLevel.Moderate, "Needed for Xbox Game Pass on PC.", false),
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar", RiskLevel.Moderate, "Removing it disables the Win+G overlay and some game features.", false),
        new("Microsoft.XboxGameOverlay", "Xbox Game Overlay", RiskLevel.Moderate, "Part of the Game Bar experience.", false),
        new("Microsoft.XboxSpeechToTextOverlay", "Xbox speech-to-text", RiskLevel.Safe, "Safe to remove.", false),
        new("Microsoft.XboxIdentityProvider", "Xbox Identity Provider", RiskLevel.Advanced, "Required for signing in to Xbox services in games. Do not remove if you play games.", true),
        new("Microsoft.Xbox.TCUI", "Xbox TCUI", RiskLevel.Advanced, "Used by games for Xbox Live features.", true),
        new("Microsoft.StorePurchaseApp", "Store purchase app", RiskLevel.Advanced, "Needed for purchases in the Microsoft Store.", true),
        new("Microsoft.WindowsStore", "Microsoft Store", RiskLevel.Advanced, "Do not remove. Without it you cannot install or update Store apps.", true),
        new("Microsoft.DesktopAppInstaller", "App Installer (winget)", RiskLevel.Advanced, "Do not remove. Provides winget and installs .msix packages.", true),
        new("Microsoft.SecHealthUI", "Windows Security", RiskLevel.Advanced, "Do not remove. This is the Windows Security interface.", true),
        new("Microsoft.WebMediaExtensions", "Web Media Extensions", RiskLevel.Advanced, "Needed to play some web video formats.", true),
        new("Microsoft.HEIFImageExtension", "HEIF Image Extension", RiskLevel.Advanced, "Needed to open HEIC photos from phones.", true),
        new("Microsoft.VP9VideoExtensions", "VP9 Video Extensions", RiskLevel.Advanced, "Needed to play VP9 video.", true),
        new("Microsoft.WebpImageExtension", "WebP Image Extension", RiskLevel.Advanced, "Needed to open WebP images.", true),
        new("Microsoft.AV1VideoExtension", "AV1 Video Extension", RiskLevel.Advanced, "Needed to play AV1 video (YouTube, Netflix).", true),
        new("Microsoft.HEVCVideoExtension", "HEVC Video Extensions", RiskLevel.Advanced, "Needed to play HEVC/H.265 video from phones and cameras.", true),
        new("Microsoft.RawImageExtension", "Raw Image Extension", RiskLevel.Advanced, "Needed to open camera RAW photos in Photos and Explorer.", true),
        new("Microsoft.Copilot", "Copilot", RiskLevel.Safe, "Safe to remove if you do not use the Copilot assistant.", false),
        new("Microsoft.Windows.Ai.Copilot.Provider", "Copilot provider", RiskLevel.Safe, "Safe to remove together with Copilot.", false),
        new("Microsoft.BingSearch", "Bing Search", RiskLevel.Safe, "Safe to remove; Start menu web search becomes local only.", false),
        new("Microsoft.WindowsCommunicationsApps", "Mail and Calendar (classic)", RiskLevel.Safe, "Safe to remove; the new Outlook replaces it.", false),
        new("Microsoft.Windows.CrossDevice", "Cross Device Experience Host", RiskLevel.Safe, "Safe to remove unless you use Phone Link features such as mobile hotspot sharing.", false),
        new("Microsoft.StartExperiencesApp", "Start menu experiences", RiskLevel.Advanced, "Do not remove. Part of the Start menu.", true),
        new("Microsoft.Windows.ShellExperienceHost", "Shell experience host", RiskLevel.Advanced, "Do not remove. Draws the taskbar and Start menu.", true),
        new("MicrosoftWindows.Client.WebExperience", "Widgets", RiskLevel.Safe, "Safe to remove; this is the Widgets board on the taskbar.", false),
        new("Microsoft.QuickAssist", "Quick Assist", RiskLevel.Safe, "Safe to remove if nobody helps you remotely with it.", false),
        new("Microsoft.WindowsAppRuntime", "Windows App Runtime", RiskLevel.Advanced, "Do not remove. Many modern apps need it.", true),
        new("Microsoft.UI.Xaml", "UI library (Microsoft.UI.Xaml)", RiskLevel.Advanced, "Do not remove. Shared library used by many apps.", true),
        new("Microsoft.VCLibs", "Visual C++ runtime (Store)", RiskLevel.Advanced, "Do not remove. Shared library used by many apps.", true),
        new("Microsoft.NET.Native", ".NET Native runtime", RiskLevel.Advanced, "Do not remove. Shared library used by many apps.", true),
        new("AdvancedMicroDevicesInc-2.AMDRadeonSoftware", "AMD Radeon Software", RiskLevel.Advanced, "Do not remove. The AMD graphics driver's control panel (FreeSync, display and game settings).", true),
        new("AdvancedMicroDevicesInc", "AMD software", RiskLevel.Advanced, "Do not remove. AMD driver software (graphics, chipset or laptop control panel).", true),
        new("NVIDIACorp.NVIDIAControlPanel", "NVIDIA Control Panel", RiskLevel.Advanced, "Do not remove. The NVIDIA graphics driver's control panel.", true),
        new("AppUp.IntelGraphicsExperience", "Intel Graphics Command Center", RiskLevel.Advanced, "Do not remove. The Intel graphics driver's control panel.", true),
        new("AppUp.IntelArcSoftware", "Intel Arc Control", RiskLevel.Advanced, "Do not remove. The Intel Arc graphics driver's control panel.", true),
        new("AppUp.ThunderboltControlCenter", "Thunderbolt Control Center", RiskLevel.Advanced, "Do not remove. Approves Thunderbolt and USB4 devices.", true),
        new("RealtekSemiconductorCorp.RealtekAudioControl", "Realtek Audio Console", RiskLevel.Advanced, "Do not remove. The Realtek audio driver's control panel.", true),
        new("DolbyLaboratories.DolbyAudio", "Dolby Audio", RiskLevel.Advanced, "Do not remove. The Dolby audio driver's control panel.", true),
        new("WavesAudio.MaxxAudio", "Waves MaxxAudio", RiskLevel.Advanced, "Do not remove. The Waves audio driver's control panel.", true),
        new("WavesAudio.WavesMaxxAudio", "Waves MaxxAudio", RiskLevel.Advanced, "Do not remove. The Waves audio driver's control panel.", true),
    ];

    /// <summary>Packages that must never be offered for removal, even in advanced mode.</summary>
    public static IReadOnlySet<string> NeverRemove { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.SecHealthUI",
        "Microsoft.StartExperiencesApp",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.UI.Xaml",
        "Microsoft.VCLibs",
        "Microsoft.NET.Native",

        // Driver control panels: removing them loses display, audio or Thunderbolt settings the driver needs.
        // Every AMD package: Radeon Software ships under different AdvancedMicroDevicesInc* names on OEM laptops
        // (for example AdvancedMicroDevicesInc-RSXCM), so the whole publisher prefix is protected.
        "AdvancedMicroDevicesInc",
        "AdvancedMicroDevicesInc-2.AMDRadeonSoftware",
        "AdvancedMicroDevicesInc-RSXCM",
        "NVIDIACorp.NVIDIAControlPanel",
        "AppUp.IntelGraphicsExperience",
        "AppUp.IntelArcSoftware", // Defensive: the Arc control panel is usually a desktop app, but block it if a Store build appears.
        "AppUp.ThunderboltControlCenter",
        "RealtekSemiconductorCorp.RealtekAudioControl",
        "DolbyLaboratories.DolbyAudio",
        "WavesAudio.MaxxAudio",
        "WavesAudio.WavesMaxxAudio",
    };

    /// <summary>
    /// Whether the app can be offered for removal: Windows must not mark it NonRemovable and it must not be on the
    /// <see cref="NeverRemove"/> list. A name with <c>*</c> wildcards (used by the Debloat page for promotional apps) is
    /// refused when it is too vague or could match a never-remove package.
    /// </summary>
    public static bool IsRemovable(string? packageName, bool nonRemovable)
    {
        if (nonRemovable || string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }

        if (packageName.Contains('*'))
        {
            if (packageName.Replace("*", string.Empty, StringComparison.Ordinal).Length < 5)
            {
                return false;
            }

            return !NeverRemove.Any(n => Debloat.DebloatRules.AppNameMatches(packageName, n) || Debloat.DebloatRules.AppNameMatches(packageName, n + ".Package"));
        }

        return !NeverRemove.Any(n => packageName.StartsWith(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds the catalog entry for a package full name or family name.</summary>
    public static Entry? Find(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        return All.FirstOrDefault(e => packageName.StartsWith(e.PackageFamilyPrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Friendly name for a package, or a cleaned-up version of the technical name.</summary>
    public static string FriendlyName(string packageName)
    {
        var entry = Find(packageName);
        if (entry is not null)
        {
            return entry.FriendlyName;
        }

        var name = packageName.Split('_')[0];
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
    }

    /// <summary>Risk for a package; unknown packages are treated as Moderate.</summary>
    public static RiskLevel RiskFor(string packageName) => Find(packageName)?.Risk ?? RiskLevel.Moderate;
}

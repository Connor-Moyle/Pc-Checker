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

        // Microsoft apps the reference tools remove by default.
        ("onenote", "Microsoft.Office.OneNote", "OneNote for Windows 10", true, "Safe to remove; OneNote from Microsoft 365 is a separate app."),
        ("devhome", "Microsoft.Windows.DevHome", "Dev Home", true, "Retired by Microsoft. Safe to remove."),
        ("journal", "Microsoft.MicrosoftJournal", "Journal", true, "Safe to remove."),
        ("mailcalendar", "microsoft.windowscommunicationsapps", "Mail and Calendar", true, "Retired by Microsoft; replaced by the new Outlook. Safe to remove."),
        ("family", "MicrosoftCorporationII.MicrosoftFamily", "Microsoft Family Safety", true, "Safe to remove; parental controls stay set in your Microsoft account."),
        ("aihub", "Microsoft.Windows.AIHub", "AI Hub", true, "Promotes AI apps in the Store. Safe to remove."),
        ("pcmanager", "Microsoft.PCManager", "Microsoft PC Manager", true, "Safe to remove; PC Checker covers its clean-up features."),
        ("m365companions", "Microsoft.M365Companions", "Microsoft 365 Companions", true, "Safe to remove."),
        ("officepush", "Microsoft.OfficePushNotificationUtility", "Office push notifications", true, "Safe to remove; installed Office apps are not affected."),
        ("stickynotes", "Microsoft.MicrosoftStickyNotes", "Sticky Notes", false, "Remove only if you don't use it; notes stay in your Microsoft account."),
        ("alarms", "Microsoft.WindowsAlarms", "Clock", false, "Remove only if you don't use alarms, timers or focus sessions."),
        ("soundrecorder", "Microsoft.WindowsSoundRecorder", "Sound Recorder", false, "Remove only if you don't record audio with it."),
        ("whiteboard", "Microsoft.Whiteboard", "Whiteboard", false, "Remove only if you don't use it."),
        ("quickassist", "MicrosoftCorporationII.QuickAssist", "Quick Assist", false, "Remove only if nobody helps you remotely with it."),
        ("windowsapp", "MicrosoftCorporationII.Windows365", "Windows App (Windows 365)", false, "Remove only if you don't use Windows 365 or Azure Virtual Desktop."),
        ("remotedesktop", "Microsoft.RemoteDesktop", "Remote Desktop (Store)", false, "Remove only if you don't connect to other PCs with it."),

        // Legacy Microsoft stubs found on upgraded and PC-maker images (most show "Not installed" on a clean 24H2).
        ("bingfood", "Microsoft.BingFoodAndDrink", "Food & Drink", true, "Retired app. Safe to remove."),
        ("binghealth", "Microsoft.BingHealthAndFitness", "Health & Fitness", true, "Retired app. Safe to remove."),
        ("bingtranslator", "Microsoft.BingTranslator", "Translator", true, "Safe to remove."),
        ("bingtravel", "Microsoft.BingTravel", "Travel", true, "Retired app. Safe to remove."),
        ("3dbuilder", "Microsoft.3DBuilder", "3D Builder", true, "Retired app. Safe to remove."),
        ("3dviewer", "Microsoft.Microsoft3DViewer", "3D Viewer", true, "Retired app. Safe to remove."),
        ("print3d", "Microsoft.Print3D", "Print 3D", true, "Retired app. Safe to remove."),
        ("paint3d", "Microsoft.MSPaint", "Paint 3D", true, "Retired app. Safe to remove; the new Paint is a separate app."),
        ("sway", "Microsoft.Office.Sway", "Sway", true, "Safe to remove."),
        ("mobileplans", "Microsoft.OneConnect", "Mobile Plans", true, "Safe to remove."),
        ("messaging", "Microsoft.Messaging", "Messaging", true, "Retired app. Safe to remove."),
        ("speedtest", "Microsoft.NetworkSpeedTest", "Network Speed Test", true, "Safe to remove; PC Checker has its own speed test."),
        ("powerbi", "Microsoft.MicrosoftPowerBIForWindows", "Power BI", true, "Safe to remove."),
        ("wallet", "Microsoft.Wallet", "Microsoft Pay", true, "Retired app. Safe to remove."),
        ("xboxcompanion", "Microsoft.XboxApp", "Xbox Console Companion", false, "Retired app; remove only if Xbox Game Bar and Xbox sign-in in games keep working without it."),

        // Third-party promotional apps. Names with * match the publisher prefix the Store gives each PC.
        ("amazon", "Amazon.com.Amazon", "Amazon", true, "Pre-installed by some PC makers. Safe to remove."),
        ("facebook", "FACEBOOK.FACEBOOK", "Facebook", true, "Pre-installed by some PC makers. Safe to remove."),
        ("instagramapp", "Facebook.Instagram", "Instagram (app)", true, "Pre-installed by some PC makers. Safe to remove."),
        ("bubblewitch", "king.com.BubbleWitch3Saga", "Bubble Witch 3 Saga", true, "Pre-installed game. Safe to remove."),
        ("hulu", "HULULLC.HULUPLUS", "Hulu", true, "Pre-installed by some PC makers. Safe to remove."),
        ("photoshopexpress", "AdobeSystemsIncorporated.AdobePhotoshopExpress", "Adobe Photoshop Express", true, "Pre-installed by some PC makers. Safe to remove."),
        ("duolingo", "*Duolingo-LearnLanguagesforFree", "Duolingo", true, "Pre-installed by some PC makers. Safe to remove."),
        ("flipboard", "*.Flipboard", "Flipboard", true, "Pre-installed by some PC makers. Safe to remove."),
        ("pandora", "PandoraMediaInc.*", "Pandora", true, "Pre-installed by some PC makers. Safe to remove."),
        ("slingtv", "*.SlingTV", "Sling TV", true, "Pre-installed by some PC makers. Safe to remove."),
        ("tunein", "*.TuneInRadio", "TuneIn Radio", true, "Pre-installed by some PC makers. Safe to remove."),
        ("iheartradio", "*.iHeartRadio", "iHeartRadio", true, "Pre-installed by some PC makers. Safe to remove."),
        ("nytcrossword", "*.NYTCrossword", "NYT Crossword", true, "Pre-installed by some PC makers. Safe to remove."),
        ("winzip", "*.WinZipUniversal", "WinZip Universal", true, "Pre-installed by some PC makers. Safe to remove."),
        ("drawboard", "*.DrawboardPDF", "Drawboard PDF", true, "Pre-installed by some PC makers. Safe to remove."),
        ("cyberlink", "*CyberLinkMediaSuiteEssentials", "CyberLink Media Suite Essentials", true, "Pre-installed by some PC makers. Safe to remove."),
        ("sketchbook", "*.AutodeskSketchBook", "Autodesk SketchBook", true, "Pre-installed by some PC makers. Safe to remove."),
        ("picsart", "*.PicsArt-PhotoStudio", "PicsArt Photo Studio", true, "Pre-installed by some PC makers. Safe to remove."),
        ("polarr", "*.PolarrPhotoEditorAcademicEdition", "Polarr Photo Editor", true, "Pre-installed by some PC makers. Safe to remove."),
        ("phototastic", "*.PhototasticCollage", "Phototastic Collage", true, "Pre-installed by some PC makers. Safe to remove."),
        ("livewallpaper", "Sidia.LiveWallpaper", "Live Wallpaper", true, "Pre-installed by some PC makers. Safe to remove."),
        ("onecalendar", "*.OneCalendar", "OneCalendar", true, "Pre-installed by some PC makers. Safe to remove."),
        ("acgmediaplayer", "*.ACGMediaPlayer", "ACG Media Player", true, "Pre-installed by some PC makers. Safe to remove."),
        ("actipro", "ActiproSoftwareLLC.*", "Actipro app", true, "Pre-installed by some PC makers. Safe to remove."),
        ("asphalt8", "*.Asphalt8Airborne", "Asphalt 8", true, "Pre-installed game. Safe to remove."),
        ("caesarsslots", "*.CaesarsSlotsFreeCasino", "Caesars Slots", true, "Pre-installed game. Safe to remove."),
        ("cookingfever", "*.COOKINGFEVER", "Cooking Fever", true, "Pre-installed game. Safe to remove."),
        ("disneykingdoms", "*.DisneyMagicKingdoms", "Disney Magic Kingdoms", true, "Pre-installed game. Safe to remove."),
        ("farmville", "*.FarmVille2CountryEscape", "FarmVille 2", true, "Pre-installed game. Safe to remove."),
        ("hiddencity", "*.HiddenCityMysteryofShadows", "Hidden City", true, "Pre-installed game. Safe to remove."),
        ("marchofempires", "*.MarchofEmpires", "March of Empires", true, "Pre-installed game. Safe to remove."),
        ("royalrevolt", "flaregamesGmbH.RoyalRevolt*", "Royal Revolt", true, "Pre-installed game. Safe to remove."),
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
        ("gamebar", "Microsoft.XboxGamingOverlay", "Xbox Game Bar", "Removing it disables the Win+G overlay, screen recording and some game features. On AMD Ryzen 9 X3D CPUs (7900X3D, 7950X3D, 9900X3D, 9950X3D) AMD's driver needs Game Bar to move games onto the V-Cache cores; keep it there."),
        ("mediaplayer", "Microsoft.ZuneMusic", "Media Player", "Media Player is the default music and video player; removing it leaves only the legacy Windows Media Player, if installed."),
    ];

    /// <summary>
    /// Apps PC makers pre-install (HP, Lenovo, Dell, Acer, ASUS, MSI, Samsung, LG and partners), on AMD and Intel
    /// systems alike. Never preselected: some control fans, battery charging or function keys.
    /// </summary>
    private static (string Id, string Package, string Name, string? Warning)[] OemApps =>
    [
        ("hp.myhp", "AD2F1837.myHP", "myHP", null),
        ("hp.supportassistant", "AD2F1837.HPSupportAssistant", "HP Support Assistant", "Also delivers HP BIOS and driver updates; use HP's website instead if you remove it."),
        ("hp.jumpstarts", "AD2F1837.HPJumpStarts", "HP JumpStarts", null),
        ("hp.welcome", "AD2F1837.HPWelcome", "HP Welcome", null),
        ("hp.privacysettings", "AD2F1837.HPPrivacySettings", "HP Privacy Settings", null),
        ("hp.sureshield", "AD2F1837.HPSureShieldAI", "HP Sure Shield AI", null),
        ("hp.aiexperience", "AD2F1837.HPAIExperienceCenter", "HP AI Experience Center", null),
        ("hp.connectedmusic", "AD2F1837.HPConnectedMusic", "HP Connected Music", null),
        ("hp.connectedphoto", "AD2F1837.HPConnectedPhotopoweredbySnapfish", "HP Connected Photo", null),
        ("hp.quickdrop", "AD2F1837.HPQuickDrop", "HP QuickDrop", null),
        ("hp.registration", "AD2F1837.HPRegistration", "HP Registration", null),
        ("hp.workwell", "AD2F1837.HPWorkWell", "HP WorkWell", null),
        ("hp.desktopsupport", "AD2F1837.HPDesktopSupportUtilities", "HP Desktop Support Utilities", null),
        ("hp.easyclean", "AD2F1837.HPEasyClean", "HP Easy Clean", null),
        ("hp.fileviewer", "AD2F1837.HPFileViewer", "HP File Viewer", null),
        ("hp.diagnostics", "AD2F1837.HPPCHardwareDiagnosticsWindows", "HP PC Hardware Diagnostics", null),
        ("hp.printercontrol", "AD2F1837.HPPrinterControl", "HP Printer Control", "Remove only if you don't manage an HP printer with it."),
        ("hp.quicktouch", "AD2F1837.HPQuickTouch", "HP QuickTouch", null),
        ("hp.systeminfo", "AD2F1837.HPSystemInformation", "HP System Information", null),
        ("hp.powermanager", "AD2F1837.HPPowerManager", "HP Power Manager", "Controls battery charging limits and power modes on HP laptops. Keep it unless you are sure you don't use them."),
        ("hp.omen", "AD2F1837.OMENCommandCenter", "OMEN Gaming Hub", "Controls fans, performance modes and lighting on OMEN and Victus PCs. Keep it unless you are sure you don't need them."),
        // Lenovo Vantage replaced Lenovo Companion under the same Store package ID.
        ("lenovo.vantage", "E046963F.LenovoCompanion", "Lenovo Vantage", "Lenovo Vantage sets battery conservation mode, function keys and firmware updates. Keep it unless you are sure you don't use them."),
        ("lenovo.smartappearance", "E0469640.SmartAppearance", "Lenovo Smart Appearance", null),
        ("dell.supportassist", "DellInc.DellSupportAssistforPCs", "Dell SupportAssist", "Also delivers Dell BIOS and driver updates; use Dell's website instead if you remove it."),
        ("dell.digitaldelivery", "DellInc.DellDigitalDelivery", "Dell Digital Delivery", null),
        ("dell.mobileconnect", "DellInc.DellMobileConnect", "Dell Mobile Connect", null),
        ("acer.carecenter", "AcerIncorporated.AcerCareCenterS", "Acer Care Center", null),
        ("acer.registration", "AcerIncorporated.AcerRegistration", "Acer Registration", null),
        ("asus.myasus", "B9ECED6F.ASUSPCAssistant", "MyASUS", "MyASUS sets battery charging limits, fan profiles and function keys on ASUS laptops. Keep it unless you are sure you don't use them."),
        ("asus.glidex", "B9ECED6F.*GlideX*", "ASUS GlideX", null),
        ("asus.screenpad", "B9ECED6F.ScreenPadMaster", "ASUS ScreenPad Master", "Needed for the second screen on ScreenPad laptops."),
        ("msi.center", "9426MICRO-STARINTERNATION.MSICenter", "MSI Center", "Controls fans, performance modes and lighting on MSI PCs. Keep it unless you are sure you don't need them."),
        ("msi.business", "9426MICRO-STARINTERNATION.BusinessCenter", "MSI Center Pro", "Controls fans and performance modes on MSI business PCs."),
        ("samsung.bixby", "SAMSUNGELECTRONICS*Bixby*", "Samsung Bixby", null),
        ("samsung.welcome", "SAMSUNGELECTRONICS*SamsungWelcome*", "Samsung Welcome", null),
        ("samsung.notes", "SAMSUNGELECTRONICS*SamsungNotes*", "Samsung Notes", "Notes that are not synced to Samsung Cloud are lost."),
        ("samsung.gallery", "SAMSUNGELECTRONICS*PCGallery*", "Samsung Gallery", null),
        ("samsung.smartthings", "SAMSUNGELECTRONICS*SmartThingsWindows*", "SmartThings", null),
        ("samsung.studio", "SAMSUNGELECTRONICS*SamsungStudio*", "Samsung Studio", null),
        ("samsung.screenrecording", "SAMSUNGELECTRONICS*SamsungScreenRecording*", "Samsung Screen Recording", null),
        ("samsung.penup", "SAMSUNGELECTRONICS*PENUP*", "PENUP", null),
        ("samsung.flux", "SAMSUNGELECTRONICS*SamsungFlux*", "Samsung Flux", null),
        ("samsung.multicontrol", "SAMSUNGELECTRONICS*MultiControl*", "Samsung Multi Control", "Shares your keyboard and mouse with a Samsung phone or tablet."),
        ("samsung.secondscreen", "SAMSUNGELECTRONICS*SecondScreen*", "Samsung Second Screen", "Uses a Samsung tablet as a second display."),
        ("samsung.quickshare", "SAMSUNGELECTRONICS*SamsungQuickShare*", "Samsung Quick Share", null),
        ("lg.monitorapp", "LGElectronics*LGMonitorApp*", "LG Monitor app", null),
        ("dolby.access", "DolbyLaboratories.DolbyAccess", "Dolby Access", "Needed for Dolby Atmos for headphones and home theatre. The Dolby audio driver panel is never offered here."),
        ("intel.mss", "AppUp.IntelManagementandSecurityStatus", "Intel Management and Security Status", null),
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

        yield return Tweak("apps.crossdevice", AppsCategory, "Remove Cross Device Experience Host",
            "Remove only if you don't use Phone Link features such as mobile hotspot sharing or your phone as a webcam. Reinstall it from the Microsoft Store any time.",
            RiskLevel.Moderate, From(Win11Debloat, AtlasOS),
            [new AppxRemovalOp("MicrosoftWindows.CrossDevice", "Cross Device Experience Host"), new AppxRemovalOp("Microsoft.Windows.CrossDevice", "Cross Device Experience Host (older)")],
            warning: "Phone Link loses mobile hotspot sharing, using your phone as a webcam and other cross-device features until you reinstall it.");

        yield return Tweak("apps.widgets", AppsCategory, "Remove Widgets completely",
            "Removes the Widgets board (Windows Web Experience Pack) and the Widgets Platform Runtime, instead of only hiding the button. Reinstall them from the Microsoft Store.",
            RiskLevel.Moderate, From(WinUtil, Win11Debloat),
            [new AppxRemovalOp("MicrosoftWindows.Client.WebExperience", "Widgets"), new AppxRemovalOp("Microsoft.WidgetsPlatformRuntime", "Widgets Platform Runtime")],
            minBuild: Windows11,
            warning: "Weather and news on the taskbar and the Win+W board stop working, and some apps' widgets go with them.");

        foreach (var app in OemApps)
        {
            yield return Tweak("apps.oem." + app.Id, AppsCategory, "Remove " + app.Name,
                "Pre-installed by the PC maker. Never ticked by a preset; reinstall it from the Microsoft Store or the maker's website.",
                RiskLevel.Moderate, From(Win11Debloat, Tiny11),
                [new AppxRemovalOp(app.Package, app.Name)],
                warning: app.Warning ?? "PC-maker apps sometimes control hardware features; remove only if you don't use it.");
        }
    }
}

using PcChecker.Core.Optimization;
using static PcChecker.Core.Debloat.DebloatSources;

namespace PcChecker.Core.Debloat;

public static partial class DebloatCatalog
{
    private const string Ceip = @"\Microsoft\Windows\Customer Experience Improvement Program\";
    private const string ConsentStore = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    private static IEnumerable<DebloatTweak> PrivacyTweaks() =>
    [
        Linked("privacy.telemetry", PrivacyCategory, "Send only required diagnostic data",
            "Sets Windows diagnostic data to the required minimum (policy AllowTelemetry = 1), turns off tailored experiences and sets the Connected User Experiences (DiagTrack) service to manual. Same change as the Optimize page's telemetry action.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, ShutUp10, Sophia, Privatezilla, DebloatWindows10, Optimizer, HardenDebloat), "telemetry.minimal"),

        Tweak("privacy.advertisingid", PrivacyCategory, "Turn off the advertising ID",
            "Stops apps from using a per-user advertising ID to show personalised ads.",
            RiskLevel.Safe, From(WinUtil, Win11Debloat, ShutUp10, Sophia, Privatezilla, Optimizer, HardenDebloat),
            [
                User(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1, null),
            ]),

        Tweak("privacy.tailored", PrivacyCategory, "Turn off tailored experiences",
            "Microsoft stops using your diagnostic data to show personalised tips, ads and recommendations.",
            RiskLevel.Safe, From(Win11Debloat, ShutUp10, Sophia, Privatezilla),
            [
                User(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1),
                User(@"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1, null),
            ]),

        Tweak("privacy.feedback", PrivacyCategory, "Never ask for feedback",
            "Windows stops popping up \"How likely are you to recommend…\" feedback requests.",
            RiskLevel.Safe, From(WinUtil, ShutUp10, Sophia, Privatezilla, DebloatWindows10, HardenDebloat),
            [
                User(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1, null),
            ]),

        Tweak("privacy.activity", PrivacyCategory, "Turn off activity history",
            "Windows stops keeping (and uploading) a history of the apps, files and sites you used.",
            RiskLevel.Safe, From(WinUtil, ShutUp10, Sophia, Privatezilla, Optimizer, HardenDebloat),
            [
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null),
            ]),

        Tweak("privacy.apptracking", PrivacyCategory, "Stop tracking app launches",
            "Windows stops recording which apps you open to rank Start and search. The \"Most used\" list in Start goes away.",
            RiskLevel.Safe, From(Win11Debloat, ShutUp10, Sophia, Privatezilla),
            [ExplorerFlag("Start_TrackProgs", 0, null)]),

        Tweak("privacy.recentdocs", PrivacyCategory, "Don't show recently opened files in Start and Jump Lists",
            "Recently opened files no longer appear in Start, Jump Lists or File Explorer's Recent list.",
            RiskLevel.Safe, From(Sophia, ShutUp10, Optimizer),
            [ExplorerFlag("Start_TrackDocs", 0, null)]),

        Tweak("privacy.inking", PrivacyCategory, "Turn off inking and typing personalisation",
            "Windows stops collecting what you type and write to build a personal dictionary, and stops sending typing insights.",
            RiskLevel.Safe, From(ShutUp10, Sophia, Privatezilla, HardenDebloat),
            [
                User(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, null),
                User(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, null),
                User(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, null),
                User(@"Software\Microsoft\Input\TIPC", "Enabled", 0, null),
            ]),

        Tweak("privacy.speech", PrivacyCategory, "Turn off online speech recognition",
            "Voice typing and dictation stop sending your voice to Microsoft's cloud. Offline speech features keep working.",
            RiskLevel.Safe, From(ShutUp10, Sophia, Privatezilla, HardenDebloat),
            [User(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0, null)]),

        Tweak("privacy.appdiagnostics", PrivacyCategory, "Block apps from reading diagnostic info about other apps",
            "Store apps can no longer read information about the other apps running on your PC.",
            RiskLevel.Safe, From(ShutUp10, Privatezilla),
            [MachineText(ConsentStore + "appDiagnostics", "Value", "Deny", "Allow")]),

        Tweak("privacy.langlist", PrivacyCategory, "Don't let websites read your language list",
            "Websites can no longer use your installed languages to tailor content (or fingerprint your PC).",
            RiskLevel.Safe, From(ShutUp10, Sophia, Privatezilla),
            [User(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1, null)]),

        Tweak("privacy.handwriting", PrivacyCategory, "Don't share handwriting data",
            "Stops handwriting samples and handwriting error reports being sent to Microsoft.",
            RiskLevel.Safe, From(ShutUp10, Privatezilla, HardenDebloat),
            [
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\TabletPC", "PreventHandwritingDataSharing", 1, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports", "PreventHandwritingErrorReports", 1, null),
            ]),

        Linked("privacy.backgroundapps", PrivacyCategory, "Stop Store apps running in the background",
            "Store apps only run while they are open. Same change as the Optimize page's background apps action.",
            RiskLevel.Safe, From(WinUtil, ShutUp10, Sophia, Privatezilla, Optimizer), "backgroundapps.disable"),

        Tweak("privacy.location", PrivacyCategory, "Turn off location access for apps",
            "Apps and Windows services can no longer ask for your location.",
            RiskLevel.Moderate, From(WinUtil, ShutUp10, Sophia, Privatezilla, HardenDebloat),
            [MachineText(ConsentStore + "location", "Value", "Deny", "Allow")],
            warning: "Maps, Weather, automatic time zone and Find my device lose your location."),

        Tweak("privacy.wer", PrivacyCategory, "Don't send error reports to Microsoft",
            "Windows Error Reporting stops uploading crash reports. Crash dumps are still written locally for troubleshooting.",
            RiskLevel.Moderate, From(ShutUp10, Sophia, Privatezilla, DebloatWindows10, AtlasOS, HardenDebloat),
            [
                Machine(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, null),
                Service("WerSvc", DebloatStartMode.Manual, DebloatStartMode.Manual),
            ],
            warning: "Microsoft no longer receives the reports it uses to offer fixes for crashing apps."),

        Tweak("privacy.ceiptasks", PrivacyCategory, "Disable telemetry scheduled tasks",
            "Disables the Customer Experience Improvement Program, compatibility appraiser, disk diagnostic and feedback data-collection tasks.",
            RiskLevel.Moderate, From(WinUtil, Sophia, Privatezilla, DebloatWindows10, Optimizer, HardenDebloat),
            [
                DisableTask(Ceip + "Consolidator"),
                DisableTask(Ceip + "UsbCeip"),
                DisableTask(Ceip + "KernelCeipTask"),
                DisableTask(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"),
                DisableTask(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"),
                DisableTask(@"\Microsoft\Windows\Autochk\Proxy"),
                DisableTask(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector"),
                DisableTask(@"\Microsoft\Windows\Feedback\Siuf\DmClient"),
                DisableTask(@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload"),
            ],
            warning: "The compatibility appraiser also checks whether a feature update fits your PC; Windows Update still works."),

        Tweak("privacy.devicemetadata", PrivacyCategory, "Don't download device companion apps and info",
            "Windows stops fetching manufacturer apps, icons and descriptions for new devices from the internet (blocks surprise installs such as vendor control panels).",
            RiskLevel.Moderate, From(WinUtil, ShutUp10),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\Device Metadata", "PreventDeviceMetadataFromNetwork", 1, null)],
            warning: "New devices show generic icons; install the vendor's app yourself if you want it."),

        Tweak("privacy.clipboardcloud", PrivacyCategory, "Turn off clipboard sync across devices",
            "Text you copy is no longer uploaded to sync with your other devices. The local clipboard history (Win+V) keeps working.",
            RiskLevel.Moderate, From(ShutUp10, HardenDebloat),
            [Machine(@"SOFTWARE\Policies\Microsoft\Windows\System", "AllowCrossDeviceClipboard", 0, null)]),

        Tweak("privacy.dmwappush", PrivacyCategory, "Disable the WAP push message routing service",
            "Disables dmwappushservice, which forwards device-management and telemetry push messages.",
            RiskLevel.Advanced, From(Sophia, DebloatWindows10, AtlasOS, HardenDebloat),
            [Service("dmwappushservice", DebloatStartMode.Disabled, DebloatStartMode.Manual)],
            advancedOnly: true,
            warning: "PCs managed by work or school (MDM/Intune) need this service. Leave it alone on such PCs."),

        Tweak("privacy.edgediag", PrivacyCategory, "Stop Microsoft Edge sending optional diagnostic data",
            "Sets Edge policies so only required diagnostic data is sent and browsing is not used to personalise ads and news.",
            RiskLevel.Advanced, From(WinUtil, ShutUp10, HardenDebloat),
            [
                Machine(@"SOFTWARE\Policies\Microsoft\Edge", "DiagnosticData", 0, null),
                Machine(@"SOFTWARE\Policies\Microsoft\Edge", "PersonalizationReportingEnabled", 0, null),
            ],
            advancedOnly: true,
            warning: "Edge shows \"Your browser is managed by your organization\" while any Edge policy is set."),
    ];
}

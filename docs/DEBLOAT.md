# Debloat

The **Debloat** page (Software group, after Windows Features) collects what the
popular Windows debloat tools do and offers it inside PC Checker's safety model:
every change is shown before it happens, starts with a restore point, saves the
previous values and can be reverted from the same page.

The coverage was compared with these open-source tools (their ideas were
re-implemented; none of their code is used):

| Tool | What it is known for |
|---|---|
| [Chris Titus Tech WinUtil](https://github.com/ChrisTitusTech/winutil) | Essential and advanced tweaks, preference toggles, undo, JSON export |
| [Raphire Win11Debloat](https://github.com/Raphire/Win11Debloat) | App removal lists, telemetry, Bing, Copilot, Recall, widgets, ads, taskbar and Explorer tweaks |
| [O&O ShutUp10++](https://www.oo-software.com/en/shutup10) | Large privacy catalog with recommended / limited / not-recommended levels |
| [Sophia Script for Windows](https://github.com/farag2/Sophia-Script-for-Windows) | Very broad, one function per tweak with enable and disable |
| [Privatezilla](https://github.com/builtbybel/privatezilla), [BloatyNosy](https://github.com/builtbybel/BloatyNosy) | Privacy checks with status, Windows 11 app and feature clean-up |
| [Debloat-Windows-10](https://github.com/W4RH4WK/Debloat-Windows-10) | Scripts for services, telemetry tasks and apps |
| [Optimizer](https://github.com/hellzerg/optimizer) | Privacy, services and UI toggles |
| [AtlasOS](https://atlasos.net) and [ReviOS](https://revi.cc) playbooks | Only their user-reversible ideas; component removal is not offered |
| [Windows-Optimize-Harden-Debloat](https://github.com/simeononsecurity/Windows-Optimize-Harden-Debloat) | Policy-based privacy hardening |

## How it works

- **Tweaks** are data (`src/PcChecker.Core/Debloat/DebloatCatalog*.cs`). Each
  one is a group of operations: a registry value, a service start type, a
  scheduled task, a Store app removal, a Windows optional feature, or (only for
  OneDrive) a reviewed PowerShell script. Tweaks that the Optimize page already
  offers (telemetry, Game Bar, Game Mode, background apps, search indexing,
  SysMain) reuse that action instead of duplicating it, and Store app
  removal goes through the Windows Features service.
- **State** is read from the PC: every row shows *Applied*, *Not applied*,
  *Partly applied* or *Not available* (with the reason, such as "Needs Windows
  11 24H2 or newer" or "Only for Windows 10"). Hover a row to see the exact
  registry values, services and tasks it touches.
- **Apply** shows a confirmation that lists every selected tweak grouped by
  risk with its exact registry, service, task, app and feature changes, then
  runs the whole selection as one guarded batch: a restore point first (always,
  for this page, whatever the global setting), then each tweak. App and Windows
  feature removals and the linked Optimize page actions are part of that batch
  and share its restore point instead of each trying to create their own. A
  batch that contains one-way changes (app or OneDrive removal) or a Windows
  optional feature change (Recall) only runs with a restore point. When
  Windows is going to refuse it (System Protection off, or one restore point
  per day already used) the page asks first, and asks again if Windows refuses
  it anyway: **Turn on System Protection** (for the system drive, then the
  batch runs with a restore point), **Continue without** (only the tweaks that
  can be reverted from this page run; one-way changes and Windows features are
  skipped and say why) or **Cancel** (nothing changes). Two tweaks that set the
  same value differently (for example two search box styles) cannot be applied
  together; the page asks you to untick one.
  Before a tweak changes anything its current values (with their exact registry
  type) are saved to `%LOCALAPPDATA%\PcChecker\debloat-capture.json`; if that
  file cannot be written the tweak is not applied and says so. Values already
  in place are not written again. Each tweak gets its own Action Journal entry
  with the values before and after.
- **Results** are checked against the PC: after a batch every tweak in it is
  read again, and each one shows *Applied*, *Already applied*, *Skipped* (with
  the reason) or *Failed* (with a plain reason). A change that Windows did not
  keep (the setting still reads as before, usually because a policy from an
  organisation or another tool sets it back) is reported as failed, not as
  applied. Applied changes that only show later say so: *restart Explorer to
  see it* (taskbar, Start, File Explorer and right-click menu settings),
  *sign out to see it* (Copilot, Recall, search and other shell policies) or
  *restart Windows to finish* (optional features, services set to Disabled,
  long paths). A summary bar counts what was applied, already applied, skipped
  and failed, and running programs are told that settings changed (as gpupdate
  and the Settings app do) so whatever can pick them up at once does.
- **Cancel** stops the batch between tweaks: a tweak that has started always
  finishes, the ones after it are reported as skipped, and everything already
  applied can be reverted as usual.
- **Revert** restores the saved values. If nothing was saved (for example the
  setting was changed by another tool) it restores the documented Windows
  default and says so. Keys that a tweak created (the classic context menu,
  Home/Gallery) are removed again.
- **One-way** changes are labelled: removed apps come back from the Microsoft
  Store, and OneDrive from microsoft.com or `winget install Microsoft.OneDrive`.
- **Restart Explorer** is offered after taskbar, Start, File Explorer and
  context menu changes, with **Restart later**. It restarts only the File
  Explorer of your own session, as you and not as administrator. A sign-out
  note and a Windows restart bar appear for the changes that need those.
- **Advanced mode** shows the service, Windows Update, Xbox service, OneDrive
  removal and policy-only tweaks. They are hidden otherwise.
- The Print Spooler and Fax tweaks are only applied when no physical printer is
  installed and no network printer is connected (per-user `\\server\printer`
  connections count); the row explains which printer keeps them on.
- **OneDrive autostart** is turned off through the Startup apps switch
  (`StartupApproved\Run`, the same one Task Manager uses) instead of deleting the
  `Run` value. OneDrive rewrites its `Run` value when it updates, so the switch
  is the change that stays put, and reverting it cannot lose the original
  command line.

### Which account the changes apply to

PC Checker runs as administrator. Most Safe tweaks are per-user settings
(`HKCU`). They go to the account signed in to Windows, even when PC Checker
runs as a *different* administrator account (you sign in as a standard user
and enter an administrator's password at the UAC prompt): the page finds the
owner of your session's File Explorer and reads and writes that account's
registry (`HKEY_USERS\<SID>`, and `HKEY_USERS\<SID>_Classes` for
`HKCU\Software\Classes`), and a note at the top of the page says whose
settings change. Their saved "before" values are kept per account, so two
people who use the same administrator password each get their own values back
on Revert.

When the signed-in account cannot be found or its settings are not loaded,
PC Checker falls back to the account it runs as and the note says so. In
that case, and for app removals for one account and the linked Optimize page
actions (Game Mode, Game Bar, background apps, telemetry), the change belongs
to the administrator account; to debloat your own account then, sign in as an
administrator or remove apps for all users. Machine-wide tweaks (`HKLM`,
services, tasks, apps removed for all users) are not affected.

## Troubleshooting

**"Debloat tweaks don't seem to apply."** This report came from real PCs and
had several causes, all fixed in this version:

- **The batch was not started.** Batches with app removals, OneDrive removal
  or the Recall feature require a restore point. With System Protection off
  (the default on many PCs) every tweak was marked *Failed* with a long
  message and nothing changed. The page now asks first and offers to turn on
  System Protection, continue without a restore point (reversible tweaks
  only) or cancel.
- **The change went to the wrong account.** When PC Checker was started with
  another administrator's password, per-user tweaks were written to that
  administrator's registry, so the person at the PC saw no change. They now go
  to the signed-in user's hive (see *Which account the changes apply to*).
- **The change was written but not shown yet.** Taskbar, Start, File Explorer
  and context-menu settings are read when File Explorer starts, shell policies
  when you sign in, and a disabled service keeps running until Windows
  restarts. Only some tweaks said so. Every result now says which step is
  needed, the page offers *Restart Explorer*, and running programs are told
  that settings changed.
- **A value of the wrong type looked applied.** A number stored as text (for
  example `HideFileExt` = `"0"` as `REG_SZ`, as some scripts and `.reg` files
  write it) counted as applied, so the row showed *Applied*, Apply was
  greyed out and Windows, which reads a `REG_DWORD`, ignored it. Such a value
  now shows *Not applied* with "Stored as REG_SZ; Windows needs REG_DWORD",
  and applying writes the right type (Revert puts the original back).
- **Results were not checked.** A tweak was shown as *Applied* when the
  write succeeded, even if Windows or a policy set it straight back. Every
  tweak is now read again after the batch and reported as failed with the
  reason when the change did not stick. Tweaks already in place (including
  linked Optimize page actions) say *Already applied* instead of looking like
  a change that did nothing.
- **The undo file could not be saved.** A brief lock on
  `debloat-capture.json` (antivirus, indexer) failed the tweak; saving now
  retries, and the message names the file and what to check.

If a tweak still fails, the result line gives Windows' reason. *Access
denied* on a policy usually means the PC is managed by an organisation (work
or school), which sets those values itself; the log (Settings, Log folder) has the
details.

## Presets

| Preset | What it ticks |
|---|---|
| **Recommended** | Safe privacy settings, ads and suggestions off, Copilot/Recall/Click to Do and AI in Paint/Notepad off, Bing web results off, file extensions shown |
| **Gaming** | Recommended + Game Mode on, Game Bar background recording off, background Store apps off |
| **Privacy max** | Recommended + location, error reporting, telemetry tasks, device metadata, clipboard sync, recent files and OneDrive prompts |
| **Minimal UI** | Taskbar buttons (Widgets, Chat, Task View, search icon), End task, more Start pins, Explorer opens on This PC, classic context menu, compact view, no Explorer promotions |
| **Remove consumer apps** | Pre-installed consumer apps (News, Weather, Solitaire, Clipchamp, Teams personal, Tips, Get Help, social and streaming stubs…). Photos, Calculator, Notepad, Paint, Terminal, Snipping Tool, Camera, Xbox and the Store are never ticked |
| **Custom** | Clears all ticks so you choose |

Presets only tick boxes; nothing changes until you choose **Apply selected**.
No preset contains Advanced-mode tweaks or two tweaks that set the same value
differently.

## Import and export

**Export** writes the ticked tweaks:

```json
{
  "format": "pcchecker-debloat",
  "version": 1,
  "exported": "2026-10-27T12:00:00.0000000+00:00",
  "tweaks": ["privacy.activity", "ai.copilot", "explorer.extensions"]
}
```

**Import** accepts that format, a plain JSON array of tweak ids, or a WinUtil
export (`{"WPFTweaks": [...]}`). WinUtil names are mapped to the equivalent
PC Checker tweaks (for example `WPFTweaksTele` → telemetry, telemetry tasks,
tailored experiences and advertising ID; `WPFTweaksRightClickMenu` → classic
context menu; `WPFTweaksWindowsAI` → Copilot, Recall, Click to Do, Paint and
Notepad AI). Names without an equivalent (such as `WPFTweaksServices` or
`WPFTweaksUTC`) are listed after the import. Importing only ticks boxes.

## Deliberately not offered

| Step offered elsewhere | Why PC Checker leaves it out |
|---|---|
| Turn off Microsoft Defender | Leaves the PC without real-time protection, and Windows turns it back on. Installing another antivirus makes Defender step aside safely. |
| Disable Windows Update | Security fixes stop arriving. Use *Delay feature updates* and *No automatic restart* here, or pause updates in Settings. |
| Remove Microsoft Edge | Edge provides WebView2, which many apps and Windows features need; outside the EEA it cannot be removed cleanly and comes back. Edge background running and nags can be turned off instead. |
| Remove the Microsoft Store | No way to install or update Store apps, App Installer (winget) or codecs. |
| Delete services or strip Windows components | Breaks Windows Update, feature updates and repair tools; only a reinstall brings them back. |
| Turn off User Account Control | Every program would run with full administrator rights. |
| Block Microsoft servers in the hosts file | Breaks Windows Update, activation, the Store and Defender updates. |
| Turn off CPU security mitigations | A small speed gain for real, exploitable security holes. |
| Turn off SmartScreen | Removes the warning before known-malicious downloads and phishing sites. |

## Report only

Some settings have no documented policy or are a personal choice, so the page
links to them instead of changing them: Find my device, pausing Windows Update,
AI actions in File Explorer and other apps, and ads personalisation in your
Microsoft account.

## All tweaks

Windows builds: 22000 = Windows 11 21H2, 22621 = 22H2, 22631 = 23H2,
26100 = 24H2. "Offered by" lists the tools above that have an equivalent.

### Privacy & telemetry

| Tweak | What it does | Offered by | Risk | Reversible |
|---|---|---|---|---|
| **Send only required diagnostic data**<br>`privacy.telemetry` | Sets Windows diagnostic data to the required minimum (policy AllowTelemetry = 1), turns off tailored experiences and sets the Connected User Experiences (DiagTrack) service to manual. Same change as the Optimize page's telemetry action. (uses the Optimize action `telemetry.minimal`) | WinUtil, Win11Debloat, ShutUp10++, Sophia Script, Privatezilla, Debloat-Windows-10, Optimizer, Windows-Optimize-Harden-Debloat | Safe | Yes |
| **Turn off the advertising ID**<br>`privacy.advertisingid` | Stops apps from using a per-user advertising ID to show personalised ads.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo\Enabled = 0`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo\DisabledByGroupPolicy = 1` | WinUtil, Win11Debloat, ShutUp10++, Sophia Script, Privatezilla, Optimizer, Windows-Optimize-Harden-Debloat | Safe | Yes |
| **Turn off tailored experiences**<br>`privacy.tailored` | Microsoft stops using your diagnostic data to show personalised tips, ads and recommendations.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy\TailoredExperiencesWithDiagnosticDataEnabled = 0`<br>`Set HKCU\Software\Policies\Microsoft\Windows\CloudContent\DisableTailoredExperiencesWithDiagnosticData = 1` | Win11Debloat, ShutUp10++, Sophia Script, Privatezilla | Safe | Yes |
| **Never ask for feedback**<br>`privacy.feedback` | Windows stops popping up "How likely are you to recommend…" feedback requests.<br>`Set HKCU\Software\Microsoft\Siuf\Rules\NumberOfSIUFInPeriod = 0`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\DoNotShowFeedbackNotifications = 1` | WinUtil, ShutUp10++, Sophia Script, Privatezilla, Debloat-Windows-10, Windows-Optimize-Harden-Debloat | Safe | Yes |
| **Turn off activity history**<br>`privacy.activity` | Windows stops keeping (and uploading) a history of the apps, files and sites you used.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\System\EnableActivityFeed = 0`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\System\PublishUserActivities = 0`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\System\UploadUserActivities = 0` | WinUtil, ShutUp10++, Sophia Script, Privatezilla, Optimizer, Windows-Optimize-Harden-Debloat | Safe | Yes |
| **Stop tracking app launches**<br>`privacy.apptracking` | Windows stops recording which apps you open to rank Start and search. The "Most used" list in Start goes away.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_TrackProgs = 0` | Win11Debloat, ShutUp10++, Sophia Script, Privatezilla | Safe | Yes |
| **Don't show recently opened files in Start and Jump Lists**<br>`privacy.recentdocs` | Recently opened files no longer appear in Start, Jump Lists or File Explorer's Recent list.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_TrackDocs = 0` | Sophia Script, ShutUp10++, Optimizer | Safe | Yes |
| **Turn off inking and typing personalisation**<br>`privacy.inking` | Windows stops collecting what you type and write to build a personal dictionary, and stops sending typing insights.<br>`Set HKCU\Software\Microsoft\InputPersonalization\RestrictImplicitTextCollection = 1`<br>`Set HKCU\Software\Microsoft\InputPersonalization\RestrictImplicitInkCollection = 1`<br>`Set HKCU\Software\Microsoft\InputPersonalization\TrainedDataStore\HarvestContacts = 0`<br>… and 1 more | ShutUp10++, Sophia Script, Privatezilla, Windows-Optimize-Harden-Debloat | Safe | Yes |
| **Turn off online speech recognition**<br>`privacy.speech` | Voice typing and dictation stop sending your voice to Microsoft's cloud. Offline speech features keep working.<br>`Set HKCU\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy\HasAccepted = 0` | ShutUp10++, Sophia Script, Privatezilla, Windows-Optimize-Harden-Debloat | Safe | Yes |
| **Block apps from reading diagnostic info about other apps**<br>`privacy.appdiagnostics` | Store apps can no longer read information about the other apps running on your PC.<br>`Set HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\appDiagnostics\Value = "Deny"` | ShutUp10++, Privatezilla | Safe | Yes |
| **Don't let websites read your language list**<br>`privacy.langlist` | Websites can no longer use your installed languages to tailor content (or fingerprint your PC).<br>`Set HKCU\Control Panel\International\User Profile\HttpAcceptLanguageOptOut = 1` | ShutUp10++, Sophia Script, Privatezilla | Safe | Yes |
| **Don't share handwriting data**<br>`privacy.handwriting` | Stops handwriting samples and handwriting error reports being sent to Microsoft.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\TabletPC\PreventHandwritingDataSharing = 1`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports\PreventHandwritingErrorReports = 1` | ShutUp10++, Privatezilla, Windows-Optimize-Harden-Debloat | Safe | Yes |
| **Stop Store apps running in the background**<br>`privacy.backgroundapps` | Store apps only run while they are open. Same change as the Optimize page's background apps action. (uses the Optimize action `backgroundapps.disable`) | WinUtil, ShutUp10++, Sophia Script, Privatezilla, Optimizer | Safe | Yes |
| **Turn off location access for apps**<br>`privacy.location` | Apps and Windows services can no longer ask for your location.<br>`Set HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location\Value = "Deny"` | WinUtil, ShutUp10++, Sophia Script, Privatezilla, Windows-Optimize-Harden-Debloat | Moderate | Yes |
| **Don't send error reports to Microsoft**<br>`privacy.wer` | Windows Error Reporting stops uploading crash reports. Crash dumps are still written locally for troubleshooting.<br>`Set HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\Disabled = 1`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting\Disabled = 1`<br>`Set service WerSvc to Manual` | ShutUp10++, Sophia Script, Privatezilla, Debloat-Windows-10, AtlasOS, Windows-Optimize-Harden-Debloat | Moderate | Yes |
| **Disable telemetry scheduled tasks**<br>`privacy.ceiptasks` | Disables the Customer Experience Improvement Program, compatibility appraiser, disk diagnostic and feedback data-collection tasks.<br>`Disable scheduled task \Microsoft\Windows\Customer Experience Improvement Program\Consolidator`<br>`Disable scheduled task \Microsoft\Windows\Customer Experience Improvement Program\UsbCeip`<br>`Disable scheduled task \Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask`<br>… and 6 more | WinUtil, Sophia Script, Privatezilla, Debloat-Windows-10, Optimizer, Windows-Optimize-Harden-Debloat | Moderate | Yes |
| **Don't download device companion apps and info**<br>`privacy.devicemetadata` | Windows stops fetching manufacturer apps, icons and descriptions for new devices from the internet (blocks surprise installs such as vendor control panels).<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\Device Metadata\PreventDeviceMetadataFromNetwork = 1` | WinUtil, ShutUp10++ | Moderate | Yes |
| **Turn off clipboard sync across devices**<br>`privacy.clipboardcloud` | Text you copy is no longer uploaded to sync with your other devices. The local clipboard history (Win+V) keeps working.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\System\AllowCrossDeviceClipboard = 0` | ShutUp10++, Windows-Optimize-Harden-Debloat | Moderate | Yes |
| **Disable the WAP push message routing service**<br>`privacy.dmwappush` | Disables dmwappushservice, which forwards device-management and telemetry push messages.<br>`Set service dmwappushservice to Disabled` | Sophia Script, Debloat-Windows-10, AtlasOS, Windows-Optimize-Harden-Debloat | Advanced (Advanced mode) | Yes |
| **Stop Microsoft Edge sending optional diagnostic data**<br>`privacy.edgediag` | Sets Edge policies so only required diagnostic data is sent and browsing is not used to personalise ads and news.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Edge\DiagnosticData = 0`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Edge\PersonalizationReportingEnabled = 0` | WinUtil, ShutUp10++, Windows-Optimize-Harden-Debloat | Advanced (Advanced mode) | Yes |

### Ads & suggestions

| Tweak | What it does | Offered by | Risk | Reversible |
|---|---|---|---|---|
| **Turn off app suggestions in Start**<br>`ads.start` | Start stops showing "suggested" apps and promoted Store content.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SystemPaneSuggestionsEnabled = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-338388Enabled = 0` | Win11Debloat, ShutUp10++, Sophia Script, Privatezilla, Debloat-Windows-10, Optimizer | Safe | Yes |
| **Turn off fun facts and tips on the lock screen**<br>`ads.lockscreen` | The lock screen stops showing tips, tricks and promotional text over the picture.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\RotatingLockScreenOverlayEnabled = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-338387Enabled = 0` | Win11Debloat, ShutUp10++, Sophia Script, Privatezilla | Safe | Yes |
| **Turn off suggested content in Settings**<br>`ads.settings` | The Settings app stops showing suggested content and offers.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-338393Enabled = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-353694Enabled = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-353696Enabled = 0` | Win11Debloat, ShutUp10++, Sophia Script | Safe | Yes |
| **Turn off tips, tricks and the welcome experience**<br>`ads.tips` | No more "Get tips and suggestions" notifications or the welcome screen after updates.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-338389Enabled = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-310093Enabled = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SoftLandingEnabled = 0` | WinUtil, Win11Debloat, ShutUp10++, Sophia Script, Privatezilla | Safe | Yes |
| **Stop automatic installs of suggested apps**<br>`ads.silentinstall` | Windows stops silently installing promoted apps (games, social apps) for your account.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SilentInstalledAppsEnabled = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\OemPreInstalledAppsEnabled = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\PreInstalledAppsEnabled = 0`<br>… and 1 more | WinUtil, Win11Debloat, Sophia Script, Privatezilla, Debloat-Windows-10, Optimizer | Safe | Yes |
| **Turn off sync provider ads in File Explorer**<br>`ads.explorersync` | File Explorer stops showing OneDrive and Microsoft 365 promotions at the top of the window.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowSyncProviderNotifications = 0` | Win11Debloat, ShutUp10++, Sophia Script | Safe | Yes |
| **Turn off promotions and account notices in Start**<br>`ads.startrecommended` · Windows 11 (build 22621+) | Removes app promotions from Start's Recommended section and the Microsoft account badges on your profile picture.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_IrisRecommendations = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_AccountNotifications = 0` | WinUtil, Win11Debloat, Sophia Script | Safe | Yes |
| **Turn off suggestion notifications**<br>`ads.notificationsuggest` | Stops the "Suggested" notifications Windows sends (for example, offers to set up your phone or try new features).<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested\Enabled = 0` | Sophia Script, ShutUp10++ | Safe | Yes |
| **Stop "Let's finish setting up your device" nags**<br>`ads.scoobe` | Windows stops showing the full-screen setup reminders that push Microsoft 365, OneDrive and Game Pass.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement\ScoobeSystemSettingEnabled = 0` | Win11Debloat, Sophia Script, ShutUp10++ | Safe | Yes |
| **Turn off Microsoft consumer experiences (policy)**<br>`ads.consumer` | The CloudContent policy that stops Windows adding third-party consumer apps and offers.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent\DisableWindowsConsumerFeatures = 1` | WinUtil, Privatezilla, Debloat-Windows-10, Windows-Optimize-Harden-Debloat | Advanced (Advanced mode) | Yes |

### AI & Copilot

| Tweak | What it does | Offered by | Risk | Reversible |
|---|---|---|---|---|
| **Turn off Windows Copilot**<br>`ai.copilot` · Windows 11 (build 22621+) | Sets the Turn off Windows Copilot policy for your account and the PC, and hides the Copilot taskbar button.<br>`Set HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot\TurnOffWindowsCopilot = 1`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot\TurnOffWindowsCopilot = 1`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowCopilotButton = 0` | WinUtil, Win11Debloat, ShutUp10++, Sophia Script, BloatyNosy | Safe | Yes |
| **Remove the Copilot app**<br>`ai.copilotapp` · Windows 11 (build 22621+) | Uninstalls the Microsoft Copilot app. You can reinstall it from the Microsoft Store.<br>`Remove the Copilot app (Microsoft.Copilot)` | WinUtil, Win11Debloat, BloatyNosy | Safe | No (one-way) |
| **Turn off Recall snapshots**<br>`ai.recall` · Windows 11 (build 26100+) | Stops Windows saving snapshots of your screen for Recall and prevents Recall from being enabled (Copilot+ PCs).<br>`Set HKCU\Software\Policies\Microsoft\Windows\WindowsAI\DisableAIDataAnalysis = 1`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI\DisableAIDataAnalysis = 1`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI\AllowRecallEnablement = 0` | WinUtil, Win11Debloat, ShutUp10++, Sophia Script | Safe | Yes |
| **Turn off the Recall Windows feature**<br>`ai.recallfeature` · Windows 11 (build 26100+) | Turns off the optional feature that contains Recall, on PCs where it is installed.<br>`Turn off the Windows feature Recall (Recall)` | WinUtil, Win11Debloat | Moderate | Yes |
| **Turn off Click to Do**<br>`ai.clicktodo` · Windows 11 (build 26100+) | Turns off the Click to Do overlay that analyses what is on your screen (Copilot+ PCs).<br>`Set HKCU\Software\Policies\Microsoft\Windows\WindowsAI\DisableClickToDo = 1`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI\DisableClickToDo = 1` | Win11Debloat, ShutUp10++ | Safe | Yes |
| **Turn off AI features in Paint**<br>`ai.paint` · Windows 11 (build 22621+) | Uses Paint's documented policies to turn off Cocreator, Generative fill and Image Creator.<br>`Set HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint\DisableCocreator = 1`<br>`Set HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint\DisableGenerativeFill = 1`<br>`Set HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint\DisableImageCreator = 1` | WinUtil, Win11Debloat | Safe | Yes |
| **Turn off AI features in Notepad**<br>`ai.notepad` · Windows 11 (build 22621+) | Uses Notepad's policy to hide Rewrite and other AI writing features.<br>`Set HKLM\SOFTWARE\Policies\WindowsNotepad\DisableAIFeatures = 1` | WinUtil, Win11Debloat | Safe | Yes |
| **Turn off Bing web results in Start search**<br>`ai.bing` | Searching from Start or the taskbar shows only results from your PC, not Bing web results.<br>`Set HKCU\Software\Policies\Microsoft\Windows\Explorer\DisableSearchBoxSuggestions = 1`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Search\BingSearchEnabled = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Search\CortanaConsent = 0` | WinUtil, Win11Debloat, ShutUp10++, Sophia Script, Privatezilla, Optimizer | Safe | Yes |
| **Turn off search highlights**<br>`ai.searchhighlights` | Removes the daily trivia, doodles and trending searches from the search box and search window.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\SearchSettings\IsDynamicSearchBoxEnabled = 0`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\EnableDynamicContentInWSB = 0` | Win11Debloat, Sophia Script, ShutUp10++ | Safe | Yes |
| **Don't search your cloud content from Start**<br>`ai.searchcloud` | Start search stops looking through your Microsoft account and work account files in the cloud.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\SearchSettings\IsAADCloudSearchEnabled = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\SearchSettings\IsMSACloudSearchEnabled = 0` | ShutUp10++, Sophia Script | Safe | Yes |
| **Turn off Cortana**<br>`ai.cortana` | Sets the Allow Cortana policy to off. Mainly affects Windows 10; on Windows 11 Cortana is already retired.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\AllowCortana = 0` | WinUtil, ShutUp10++, Privatezilla, Debloat-Windows-10, Optimizer, Windows-Optimize-Harden-Debloat | Safe | Yes |

### Taskbar & Start

| Tweak | What it does | Offered by | Risk | Reversible |
|---|---|---|---|---|
| **Align taskbar icons to the left**<br>`taskbar.alignleft` · Windows 11 (build 22000+) | Moves the Start button and taskbar icons to the left, like Windows 10.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarAl = 0` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy | Safe | Yes |
| **Centre taskbar icons**<br>`taskbar.aligncenter` · Windows 11 (build 22000+) | Puts the Start button and taskbar icons back in the middle (the Windows 11 default).<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarAl = 1` | WinUtil, Sophia Script | Safe | Yes |
| **Hide the Widgets button**<br>`taskbar.widgets` · Windows 11 (build 22000+) | Removes the weather/news Widgets button from the taskbar.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDa = 0` | WinUtil, Win11Debloat, ShutUp10++, Sophia Script | Safe | Yes |
| **Turn off Widgets completely (policy)**<br>`taskbar.widgetspolicy` · Windows 11 (build 22000+) | Sets the Allow widgets policy to off, which removes the Widgets board for every user.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests = 0` | WinUtil, Win11Debloat, ShutUp10++ | Advanced (Advanced mode) | Yes |
| **Hide the Chat (Teams) button**<br>`taskbar.chat` · Windows 11 (build 22000+) | Removes the Microsoft Teams Chat button from the taskbar.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarMn = 0` | WinUtil, Win11Debloat, Sophia Script | Safe | Yes |
| **Hide the Task View button**<br>`taskbar.taskview` | Removes the Task View button. Win+Tab still opens Task View.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowTaskViewButton = 0` | WinUtil, Win11Debloat, Sophia Script | Safe | Yes |
| **Show search as an icon only**<br>`taskbar.searchicon` | Replaces the wide search box on the taskbar with a small search icon.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Search\SearchboxTaskbarMode = 1` | WinUtil, Win11Debloat, Sophia Script | Safe | Yes |
| **Show the full search box**<br>`taskbar.searchbox` | Shows the wide search box on the taskbar (the Windows 11 default).<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Search\SearchboxTaskbarMode = 2` | WinUtil, Sophia Script | Safe | Yes |
| **Show search as an icon with a label**<br>`taskbar.searchlabel` · Windows 11 22H2 (build 22621+) | Shows a compact search button with the word Search next to the icon.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Search\SearchboxTaskbarMode = 3` | Sophia Script | Safe | Yes |
| **Hide search on the taskbar**<br>`taskbar.searchhide` | Removes search from the taskbar completely. Press the Windows key and type to search.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Search\SearchboxTaskbarMode = 0` | WinUtil, Win11Debloat, Sophia Script | Safe | Yes |
| **Add "End task" to taskbar right-click**<br>`taskbar.endtask` · Windows 11 (build 22631+) | Right-clicking an app on the taskbar offers End task to close a frozen app without Task Manager.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings\TaskbarEndTask = 1` | WinUtil, Win11Debloat, Sophia Script | Safe | Yes |
| **Show seconds in the taskbar clock**<br>`taskbar.seconds` · Windows 11 (build 22621+) | The clock shows seconds. Uses a little more power on laptops.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowSecondsInSystemClock = 1` | Sophia Script | Safe | Yes |
| **Show more pins in Start**<br>`start.morepins` · Windows 11 (build 22621+) | Start shows an extra row of pinned apps and fewer recommendations.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_Layout = 1` | Sophia Script | Safe | Yes |
| **Hide the Settings Home page**<br>`start.settingshome` · Windows 11 (build 22631+) | Settings opens straight to System instead of the Home page with its account and Microsoft 365 cards.<br>`Set HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\SettingsPageVisibility = "hide:home"` | WinUtil | Moderate | Yes |
| **Turn off News and interests (Windows 10)**<br>`taskbar.news` · Windows 10 only | Removes the weather and news panel from the Windows 10 taskbar.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Feeds\ShellFeedsTaskbarViewMode = 2` | WinUtil, ShutUp10++, Sophia Script | Safe | Yes |
| **Hide Meet Now (Windows 10)**<br>`taskbar.meetnow` · Windows 10 only | Removes the Skype Meet Now icon from the Windows 10 notification area.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\HideSCAMeetNow = 1` | Sophia Script, Optimizer | Safe | Yes |

### File Explorer

| Tweak | What it does | Offered by | Risk | Reversible |
|---|---|---|---|---|
| **Show file extensions**<br>`explorer.extensions` | Shows endings such as .exe and .pdf so you can spot fake documents that are really programs.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\HideFileExt = 0` | WinUtil, Win11Debloat, Sophia Script, Optimizer, BloatyNosy | Safe | Yes |
| **Show hidden files and folders**<br>`explorer.hidden` | File Explorer shows hidden items (shown faded). Protected system files stay hidden.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Hidden = 1` | WinUtil, Win11Debloat, Sophia Script, Optimizer | Safe | Yes |
| **Open File Explorer to This PC**<br>`explorer.thispc` | File Explorer opens on your drives instead of Home/Quick access.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\LaunchTo = 1` | Win11Debloat, Sophia Script, Optimizer | Safe | Yes |
| **Use the classic right-click menu**<br>`explorer.classicmenu` · Windows 11 (build 22000+) | Brings back the full Windows 10 style right-click menu instead of "Show more options".<br>`Set HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32\(Default) = (empty)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy | Safe | Yes |
| **Remove Home and Gallery from the navigation pane**<br>`explorer.homegallery` · Windows 11 (build 22621+) | Hides the Home and Gallery entries on the left side of File Explorer.<br>`Set HKCU\Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}\System.IsPinnedToNameSpaceTree = 0`<br>`Set HKCU\Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}\System.IsPinnedToNameSpaceTree = 0` | WinUtil, Win11Debloat | Moderate | Yes |
| **Use compact view**<br>`explorer.compact` · Windows 11 (build 22000+) | Reduces the spacing between items in File Explorer so more files fit on screen.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\UseCompactMode = 1` | Sophia Script | Safe | Yes |
| **Hide recent files and frequent folders in Quick access**<br>`explorer.recent` | Quick access/Home stops listing files and folders you opened recently.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ShowRecent = 0`<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ShowFrequent = 0` | Sophia Script, ShutUp10++, Optimizer | Safe | Yes |
| **Hide files from Office.com in Home**<br>`explorer.officecloud` · Windows 11 (build 22621+) | File Explorer's Home stops listing your Microsoft 365 cloud files.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ShowCloudFilesInQuickAccess = 0` | Sophia Script, ShutUp10++ | Safe | Yes |
| **Show the full path in the title bar**<br>`explorer.fullpath` | Shows the complete folder path (for example C:\Users\You\Documents) in the window title.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState\FullPath = 1` | Sophia Script, Optimizer | Safe | Yes |
| **Turn off automatic folder type discovery**<br>`explorer.autodiscovery` | Stops File Explorer guessing a folder's type (Pictures, Music…) from its content, which makes big folders open faster.<br>`Set HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell\FolderType = "NotSpecified"` | WinUtil | Safe | Yes |
| **Allow long file paths**<br>`explorer.longpaths` | Lets programs that support it use paths longer than 260 characters (common in developer tools and deep folders).<br>`Set HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled = 1` | WinUtil, Sophia Script | Safe | Yes |

### Edge & OneDrive

| Tweak | What it does | Offered by | Risk | Reversible |
|---|---|---|---|---|
| **Stop Microsoft Edge running in the background**<br>`edge.background` | Turns off Startup boost and background mode, so Edge only runs while its window is open.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Edge\StartupBoostEnabled = 0`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Edge\BackgroundModeEnabled = 0` | WinUtil, ShutUp10++, Optimizer | Moderate | Yes |
| **Turn off Edge sidebar, shopping and first-run nags**<br>`edge.nags` | Hides the Edge sidebar (including Copilot), the shopping assistant, product recommendations and the first-run setup pages.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Edge\HubsSidebarEnabled = 0`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Edge\EdgeShoppingAssistantEnabled = 0`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Edge\ShowRecommendationsEnabled = 0`<br>… and 1 more | WinUtil, ShutUp10++, Optimizer | Moderate | Yes |
| **Stop OneDrive starting with Windows**<br>`onedrive.autostart` | Marks OneDrive as disabled in Startup apps (the same switch Task Manager uses). OneDrive still works when you open it.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\OneDrive = hex:030000000000000000000000` | Optimizer, Sophia Script | Safe | Yes |
| **Turn off OneDrive backup prompts and notifications**<br>`onedrive.nags` | Stops OneDrive notifications and the prompts that ask to back up Desktop, Documents and Pictures to OneDrive.<br>`Set HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Microsoft.SkyDrive.Desktop\Enabled = 0`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\OneDrive\KFMBlockOptIn = 1` | ShutUp10++, Sophia Script | Safe | Yes |
| **Uninstall OneDrive**<br>`onedrive.remove` | Runs Microsoft's OneDrive setup with /uninstall. Your files stay in the OneDrive folder on this PC; syncing stops.<br>Runs Microsoft's OneDriveSetup.exe /uninstall (nothing is downloaded). | WinUtil, Win11Debloat, Sophia Script, Optimizer, Debloat-Windows-10, BloatyNosy | Advanced (Advanced mode) | No (one-way) |

### Gaming & Xbox

| Tweak | What it does | Offered by | Risk | Reversible |
|---|---|---|---|---|
| **Turn off Game Bar background recording**<br>`gaming.gamedvr` | Stops the Xbox Game Bar capturing gameplay in the background. Same change as the Optimize page's Game Bar action. (uses the Optimize action `gamebar.disable`) | WinUtil, Win11Debloat, Sophia Script, Optimizer | Safe | Yes |
| **Turn on Game Mode**<br>`gaming.gamemode` | Windows gives games priority over background work. Same change as the Optimize page's Game Mode action. (uses the Optimize action `gamemode.enable`) | WinUtil, Sophia Script | Safe | Yes |
| **Turn off fullscreen optimizations**<br>`gaming.fso` | Makes games use true exclusive fullscreen. Can lower input latency in older DirectX games.<br>`Set HKCU\System\GameConfigStore\GameDVR_FSEBehaviorMode = 2`<br>`Set HKCU\System\GameConfigStore\GameDVR_HonorUserFSEBehaviorMode = 1`<br>`Set HKCU\System\GameConfigStore\GameDVR_FSEBehavior = 2`<br>… and 1 more | WinUtil, Optimizer, AtlasOS | Moderate | Yes |
| **Disable Xbox services**<br>`gaming.xboxservices` | Disables Xbox Live auth, game saves, networking and accessory services for PCs that never use Xbox features.<br>`Set service XblAuthManager to Disabled`<br>`Set service XblGameSave to Disabled`<br>`Set service XboxNetApiSvc to Disabled`<br>… and 1 more | WinUtil, Debloat-Windows-10, AtlasOS, Windows-Optimize-Harden-Debloat | Advanced (Advanced mode) | Yes |

### Windows Update

| Tweak | What it does | Offered by | Risk | Reversible |
|---|---|---|---|---|
| **Don't install drivers through Windows Update**<br>`wu.drivers` | Windows Update stops replacing your drivers. Use PC Checker's Drivers page or your vendor's tools instead.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\ExcludeWUDriversInQualityUpdate = 1` | WinUtil, ShutUp10++, Sophia Script | Advanced (Advanced mode) | Yes |
| **Delay feature updates by 180 days**<br>`wu.deferfeature` | New Windows versions install about six months after release, once early problems are fixed. Security updates are not delayed.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\DeferFeatureUpdates = 1`<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\DeferFeatureUpdatesPeriodInDays = 180` | WinUtil, ShutUp10++ | Advanced (Advanced mode) | Yes |
| **No automatic restart while you are signed in**<br>`wu.noautoreboot` | Windows waits for you to restart after installing updates instead of restarting on its own.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU\NoAutoRebootWithLoggedOnUsers = 1` | WinUtil, ShutUp10++, Sophia Script | Advanced (Advanced mode) | Yes |
| **Share update downloads only on your local network**<br>`wu.dolan` | Delivery Optimization stops uploading updates to other PCs on the internet; it still shares between PCs at home.<br>`Set HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\DODownloadMode = 1` | WinUtil, ShutUp10++, Sophia Script, Privatezilla | Advanced (Advanced mode) | Yes |

### Background services

| Tweak | What it does | Offered by | Risk | Reversible |
|---|---|---|---|---|
| **Disable the Fax service**<br>`svc.fax` | Safe if you never send faxes from this PC. Applied only when no physical printer is installed (multifunction printers can fax through it).<br>`Set service Fax to Disabled` | WinUtil, Debloat-Windows-10, AtlasOS, ReviOS | Advanced (Advanced mode) | Yes |
| **Disable the Print Spooler**<br>`svc.spooler` | Safe if you never print. Applied only when no physical printer is installed.<br>`Set service Spooler to Disabled` | AtlasOS, ReviOS, Windows-Optimize-Harden-Debloat | Advanced (Advanced mode) | Yes |
| **Keep Remote Registry disabled**<br>`svc.remoteregistry` | Makes sure other computers cannot edit this PC's registry over the network. Already disabled on most PCs.<br>`Set service RemoteRegistry to Disabled` | Debloat-Windows-10, Windows-Optimize-Harden-Debloat, AtlasOS | Advanced (Advanced mode) | Yes |
| **Set Downloaded Maps Manager to manual**<br>`svc.mapsbroker` | Safe if you don't use offline maps; the Maps app starts the service when it needs it.<br>`Set service MapsBroker to Manual` | WinUtil, Debloat-Windows-10, AtlasOS | Advanced (Advanced mode) | Yes |
| **Disable the Retail Demo service**<br>`svc.retaildemo` | Only used by store display PCs. Safe on every home PC.<br>`Set service RetailDemo to Disabled` | WinUtil, Debloat-Windows-10, AtlasOS | Advanced (Advanced mode) | Yes |
| **Disable Windows Media Player network sharing**<br>`svc.wmpnetwork` | Safe if you don't stream your media library to other devices with Windows Media Player.<br>`Set service WMPNetworkSvc to Disabled` | Debloat-Windows-10, Windows-Optimize-Harden-Debloat, AtlasOS | Advanced (Advanced mode) | Yes |
| **Disable the Windows Insider service**<br>`svc.wisvc` | Safe if you are not in the Windows Insider Program.<br>`Set service wisvc to Disabled` | AtlasOS, ReviOS | Advanced (Advanced mode) | Yes |
| **Disable Windows Search indexing**<br>`svc.searchindex` | Stops the background indexer. Same change as the Optimize page's search indexing action. (uses the Optimize action `search.indexing.disable`) | WinUtil, Optimizer, AtlasOS | Advanced (Advanced mode) | Yes |
| **Disable SysMain (Superfetch)**<br>`svc.sysmain` | Stops the service that preloads apps into memory. Same change as the Optimize page's SysMain action. (uses the Optimize action `sysmain.disable`) | WinUtil, Optimizer, AtlasOS, ReviOS | Advanced (Advanced mode) | Yes |

### Apps

| Tweak | What it does | Offered by | Risk | Reversible |
|---|---|---|---|---|
| **Remove News**<br>`apps.news` | Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the News app (Microsoft.BingNews)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Weather**<br>`apps.weather` | Safe to remove; weather stays available in Widgets. Reinstall it from the Microsoft Store any time.<br>`Remove the Weather app (Microsoft.BingWeather)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Money**<br>`apps.money` | Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Money app (Microsoft.BingFinance)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Sports**<br>`apps.sports` | Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Sports app (Microsoft.BingSports)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Bing Search**<br>`apps.bingsearch` | Safe to remove; Start search becomes local only. Reinstall it from the Microsoft Store any time.<br>`Remove the Bing Search app (Microsoft.BingSearch)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Solitaire Collection**<br>`apps.solitaire` | Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Solitaire Collection app (Microsoft.MicrosoftSolitaireCollection)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Get Help**<br>`apps.gethelp` | Safe to remove; support is still available on the web. Reinstall it from the Microsoft Store any time.<br>`Remove the Get Help app (Microsoft.GetHelp)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Tips**<br>`apps.tips` | Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Tips app (Microsoft.Getstarted)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Feedback Hub**<br>`apps.feedbackhub` | Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Feedback Hub app (Microsoft.WindowsFeedbackHub)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove People**<br>`apps.people` | Safe to remove; contacts stay in your account. Reinstall it from the Microsoft Store any time.<br>`Remove the People app (Microsoft.People)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Maps**<br>`apps.maps` | Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Maps app (Microsoft.WindowsMaps)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Microsoft Teams (personal)**<br>`apps.teams` | Safe to remove; Teams for work is a separate install. Reinstall it from the Microsoft Store any time.<br>`Remove the Microsoft Teams (personal) app (MicrosoftTeams)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Microsoft Teams (new)**<br>`apps.msteams` | Remove only if you don't use Teams for work or school. Reinstall it from the Microsoft Store any time.<br>`Remove the Microsoft Teams (new) app (MSTeams)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Clipchamp**<br>`apps.clipchamp` | Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Clipchamp app (Clipchamp.Clipchamp)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Microsoft 365 (Office hub)**<br>`apps.officehub` | Safe to remove; installed Office apps are not affected. Reinstall it from the Microsoft Store any time.<br>`Remove the Microsoft 365 (Office hub) app (Microsoft.MicrosoftOfficeHub)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Skype**<br>`apps.skype` | Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Skype app (Microsoft.SkypeApp)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Power Automate**<br>`apps.powerautomate` | Safe to remove unless you build desktop automations. Reinstall it from the Microsoft Store any time.<br>`Remove the Power Automate app (Microsoft.PowerAutomateDesktop)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Mixed Reality Portal**<br>`apps.mixedreality` | Safe to remove unless you own a Mixed Reality headset. Reinstall it from the Microsoft Store any time.<br>`Remove the Mixed Reality Portal app (Microsoft.MixedReality.Portal)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Movies & TV**<br>`apps.moviestv` | Safe to remove; purchases stay in your account. Reinstall it from the Microsoft Store any time.<br>`Remove the Movies & TV app (Microsoft.ZuneVideo)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Cortana**<br>`apps.cortana` | Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Cortana app (Microsoft.549981C3F5F10)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Microsoft To Do**<br>`apps.todo` | Safe to remove; tasks stay in your account. Reinstall it from the Microsoft Store any time.<br>`Remove the Microsoft To Do app (Microsoft.Todos)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Phone Link**<br>`apps.phonelink` | Remove only if you don't link a phone to this PC. Reinstall it from the Microsoft Store any time.<br>`Remove the Phone Link app (Microsoft.YourPhone)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Outlook (new)**<br>`apps.outlook` | Remove only if you use another mail app. Reinstall it from the Microsoft Store any time.<br>`Remove the Outlook (new) app (Microsoft.OutlookForWindows)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove LinkedIn**<br>`apps.linkedin` | Pre-installed by some PC makers. Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the LinkedIn app (7EE7776C.LinkedInforWindows)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Spotify (pre-installed)**<br>`apps.spotify` | Pre-installed by some PC makers. Reinstall from the Store if you use it. Reinstall it from the Microsoft Store any time.<br>`Remove the Spotify (pre-installed) app (SpotifyAB.SpotifyMusic)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Disney+**<br>`apps.disney` | Pre-installed by some PC makers. Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Disney+ app (Disney.37853FC22B2CE)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove TikTok**<br>`apps.tiktok` | Pre-installed by some PC makers. Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the TikTok app (BytedancePte.Ltd.TikTok)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Instagram**<br>`apps.instagram` | Pre-installed by some PC makers. Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Instagram app (Facebook.InstagramBeta)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Prime Video**<br>`apps.primevideo` | Pre-installed by some PC makers. Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Prime Video app (AmazonVideo.PrimeVideo)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Netflix**<br>`apps.netflix` | Pre-installed by some PC makers. Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Netflix app (4DF9E0F8.Netflix)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Candy Crush Saga**<br>`apps.candycrush` | Pre-installed game. Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Candy Crush Saga app (king.com.CandyCrushSaga)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Candy Crush Soda Saga**<br>`apps.candycrushsoda` | Pre-installed game. Safe to remove. Reinstall it from the Microsoft Store any time.<br>`Remove the Candy Crush Soda Saga app (king.com.CandyCrushSodaSaga)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Safe | No (one-way) |
| **Remove Camera**<br>`apps.camera` | A useful built-in app. Remove it only on purpose; reinstall it from the Microsoft Store.<br>`Remove the Camera app (Microsoft.WindowsCamera)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Moderate | No (one-way) |
| **Remove Calculator**<br>`apps.calculator` | A useful built-in app. Remove it only on purpose; reinstall it from the Microsoft Store.<br>`Remove the Calculator app (Microsoft.WindowsCalculator)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Moderate | No (one-way) |
| **Remove Photos**<br>`apps.photos` | A useful built-in app. Remove it only on purpose; reinstall it from the Microsoft Store.<br>`Remove the Photos app (Microsoft.Windows.Photos)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Moderate | No (one-way) |
| **Remove Paint**<br>`apps.paint` | A useful built-in app. Remove it only on purpose; reinstall it from the Microsoft Store.<br>`Remove the Paint app (Microsoft.Paint)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Moderate | No (one-way) |
| **Remove Notepad**<br>`apps.notepad` | A useful built-in app. Remove it only on purpose; reinstall it from the Microsoft Store.<br>`Remove the Notepad app (Microsoft.WindowsNotepad)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Moderate | No (one-way) |
| **Remove Snipping Tool**<br>`apps.snippingtool` | A useful built-in app. Remove it only on purpose; reinstall it from the Microsoft Store.<br>`Remove the Snipping Tool app (Microsoft.ScreenSketch)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Moderate | No (one-way) |
| **Remove Windows Terminal**<br>`apps.terminal` | A useful built-in app. Remove it only on purpose; reinstall it from the Microsoft Store.<br>`Remove the Windows Terminal app (Microsoft.WindowsTerminal)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Moderate | No (one-way) |
| **Remove Xbox app**<br>`apps.xbox` | A useful built-in app. Remove it only on purpose; reinstall it from the Microsoft Store.<br>`Remove the Xbox app app (Microsoft.GamingApp)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Moderate | No (one-way) |
| **Remove Xbox Game Bar**<br>`apps.gamebar` | A useful built-in app. Remove it only on purpose; reinstall it from the Microsoft Store.<br>`Remove the Xbox Game Bar app (Microsoft.XboxGamingOverlay)` | WinUtil, Win11Debloat, Sophia Script, BloatyNosy, Debloat-Windows-10, Optimizer | Moderate | No (one-way) |

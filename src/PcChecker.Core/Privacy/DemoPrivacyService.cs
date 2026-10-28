using PcChecker.Core.Common;

namespace PcChecker.Core.Privacy;

/// <summary>
/// Sample camera, microphone and location use for demo mode and screenshots: Discord on a call right now, a Teams
/// meeting earlier, OBS, Steam voice chat and the Weather app. Changes only touch the sample data.
/// </summary>
public sealed class DemoPrivacyService : IPrivacyService
{
    private readonly object _gate = new();
    private readonly List<CapabilityAppUsage> _apps;

    public DemoPrivacyService()
    {
        var now = DateTimeOffset.Now;
        string user = Environment.UserName;
        string discord = $@"C:\Users\{user}\AppData\Local\Discord\app-1.0.9188\Discord.exe";
        const string obs = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
        const string steam = @"C:\Program Files (x86)\Steam\steam.exe";
        const string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        _apps =
        [
            Desktop(CapabilityKind.Camera, discord, "Discord", now.AddMinutes(-18), null),
            Store(CapabilityKind.Camera, "MSTeams_8wekyb3d8bbwe", "Microsoft Teams", now.AddHours(-2).AddMinutes(-47), TimeSpan.FromMinutes(47)),
            Desktop(CapabilityKind.Camera, obs, "OBS Studio", now.AddDays(-3).AddHours(-4), TimeSpan.FromMinutes(112)),
            Store(CapabilityKind.Camera, "Microsoft.WindowsCamera_8wekyb3d8bbwe", "Camera", now.AddDays(-6), TimeSpan.FromMinutes(2)),

            Desktop(CapabilityKind.Microphone, discord, "Discord", now.AddMinutes(-18), null),
            Store(CapabilityKind.Microphone, "MSTeams_8wekyb3d8bbwe", "Microsoft Teams", now.AddHours(-2).AddMinutes(-47), TimeSpan.FromMinutes(47)),
            Desktop(CapabilityKind.Microphone, steam, "Steam", now.AddDays(-1).AddHours(-2), TimeSpan.FromMinutes(70)),
            Store(CapabilityKind.Microphone, "Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe", "Sound Recorder", now.AddDays(-9), TimeSpan.FromSeconds(48)),

            Store(CapabilityKind.Location, "Microsoft.BingWeather_8wekyb3d8bbwe", "Weather", now.AddHours(-3), TimeSpan.FromSeconds(4)),
            Desktop(CapabilityKind.Location, edge, "Microsoft Edge", now.AddDays(-2).AddHours(-5), TimeSpan.FromSeconds(11)),
            Store(CapabilityKind.Location, "Microsoft.WindowsMaps_8wekyb3d8bbwe", "Maps", now.AddDays(-20), TimeSpan.FromMinutes(6)),
        ];
    }

    public async Task<PrivacySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 150, 400).ConfigureAwait(false);
        lock (_gate)
        {
            var capabilities = Enum.GetValues<CapabilityKind>()
                .Select(c => new CapabilityStatus(c, ConsentValue.Allow, ConsentValue.Allow, ConsentValue.Allow,
                    ConsentStoreParser.Merge(_apps.Where(a => a.Capability == c))))
                .ToList();
            return new PrivacySnapshot(capabilities, [], null);
        }
    }

    public Task<OperationResult> SetAppAccessAsync(CapabilityAppUsage app, ConsentValue value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.IsPackaged)
        {
            return Task.FromResult(OperationResult.Fail("Desktop apps share one switch in Windows Settings; open the privacy page to change it."));
        }

        lock (_gate)
        {
            int i = _apps.FindIndex(a => a.Capability == app.Capability && a.KeyName == app.KeyName);
            if (i >= 0)
            {
                _apps[i] = _apps[i] with { Value = value == ConsentValue.Unknown ? ConsentValue.Allow : value };
            }
        }

        string capability = ConsentStoreParser.Name(app.Capability).ToLowerInvariant();
        return Task.FromResult(OperationResult.Ok(value == ConsentValue.Deny
            ? $"{app.DisplayName} can no longer use the {capability} (demo)."
            : $"{app.DisplayName}'s {capability} access is back on (demo)."));
    }

    public OperationResult OpenSettings(CapabilityKind capability) =>
        OperationResult.Ok($"Would open {ConsentStoreParser.SettingsUri(capability)} (demo).");

    private static CapabilityAppUsage Store(CapabilityKind capability, string family, string name, DateTimeOffset start, TimeSpan duration) =>
        new(capability, family, name, null, true, start.ToUniversalTime(), (start + duration).ToUniversalTime(), false, ConsentValue.Allow, ConsentScope.CurrentUser);

    private static CapabilityAppUsage Desktop(CapabilityKind capability, string path, string name, DateTimeOffset start, TimeSpan? duration) =>
        new(capability, path.Replace('\\', '#'), name, path, false, start.ToUniversalTime(),
            duration is { } d ? (start + d).ToUniversalTime() : null, duration is null, ConsentValue.Allow, ConsentScope.CurrentUser);
}

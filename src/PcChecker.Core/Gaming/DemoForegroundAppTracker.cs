using PcChecker.Core.Common;

namespace PcChecker.Core.Gaming;

/// <summary>Demo foreground tracking: Cyberpunk 2077 runs full screen in front; a handful of sample apps are running.</summary>
public sealed class DemoForegroundAppTracker : IForegroundAppTracker, IDisposable
{
    public const int GamePid = 18452;
    public const string GameExe = "Cyberpunk2077.exe";

    private static readonly IReadOnlyList<RunningApp> Apps =
    [
        new(GamePid, "Cyberpunk2077", @"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", "Cyberpunk 2077 (C) 2020 by CD Projekt RED", 31.4, 97.0, 9_216L * 1024 * 1024),
        new(20112, "eldenring", @"C:\Program Files (x86)\Steam\steamapps\common\ELDEN RING\Game\eldenring.exe", "ELDEN RING™", 4.2, 1.5, 6_144L * 1024 * 1024),
        new(9876, "Discord", @"C:\Users\Player\AppData\Local\Discord\app-1.0.9170\Discord.exe", "#general | Friends - Discord", 2.1, 0.8, 512L * 1024 * 1024),
        new(7740, "steamwebhelper", @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", "Steam", 1.3, 0.4, 420L * 1024 * 1024),
        new(11320, "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", "YouTube - Google Chrome", 3.6, 2.2, 1_310L * 1024 * 1024),
        new(6604, "obs64", @"C:\Program Files\obs-studio\bin\64bit\obs64.exe", "OBS 30.2.3 - Profile: Untitled", 5.8, 6.1, 380L * 1024 * 1024),
    ];

    private Timer? _announce;
    private int _references;

    public ForegroundApp? Current { get; } = new(GamePid, "Cyberpunk2077", Apps[0].ExePath, "Cyberpunk 2077 (C) 2020 by CD Projekt RED", IsFullscreen: true);

    public event EventHandler<ForegroundApp>? ForegroundChanged;

    public void Start()
    {
        if (Interlocked.Increment(ref _references) == 1)
        {
            _announce = new Timer(_ =>
            {
                if (Current is { } app)
                {
                    ForegroundChanged?.Invoke(this, app);
                }
            }, null, TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
    }

    public void Stop()
    {
        if (Interlocked.Decrement(ref _references) <= 0)
        {
            _references = 0;
            _announce?.Dispose();
            _announce = null;
        }
    }

    public async Task<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken cancellationToken)
    {
        await DemoData.DelayAsync(cancellationToken, 300, 600).ConfigureAwait(false);
        return ProcessMetrics.Rank(Apps);
    }

    public bool IsProcessRunning(int processId) => Apps.Any(a => a.Pid == processId);

    /// <summary>Sample games never exit, so the watch never fires.</summary>
    public IDisposable WatchProcessExit(int processId, Action onExited) => new NoWatch();

    public void Dispose() => _announce?.Dispose();

    private sealed class NoWatch : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

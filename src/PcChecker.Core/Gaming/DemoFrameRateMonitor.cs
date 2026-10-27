using PcChecker.Core.Common;

namespace PcChecker.Core.Gaming;

/// <summary>Demo frame counter: about 144 FPS with a little jitter for "Cyberpunk2077.exe", updated twice a second.</summary>
public sealed class DemoFrameRateMonitor : IFrameRateMonitor, IDisposable
{
    private readonly Random _random = DemoData.CreateRandom(144);
    private readonly object _gate = new();
    private Timer? _timer;
    private int? _target = DemoForegroundAppTracker.GamePid;

    public DemoFrameRateMonitor()
    {
        Publish();
    }

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public int? TargetProcessId
    {
        get
        {
            lock (_gate)
            {
                return _target;
            }
        }
    }

    public FrameRateSample? Latest { get; private set; }

    public event EventHandler<FrameRateSample>? Updated;

    public void Track(int? processId)
    {
        lock (_gate)
        {
            _target = processId;
        }

        Publish();
    }

    public void Start()
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => Publish(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose() => Stop();

    private void Publish()
    {
        FrameRateSample? sample;
        lock (_gate)
        {
            if (_target is not { } pid)
            {
                Latest = null;
                return;
            }

            double fps = Math.Round(DemoData.Jitter(_random, 144, 6), 1);
            double low = Math.Round(DemoData.Jitter(_random, 101, 4), 1);
            string name = pid == DemoForegroundAppTracker.GamePid ? DemoForegroundAppTracker.GameExe : $"process {pid}";
            sample = new FrameRateSample(pid, name, fps, 1000.0 / fps, low, DateTimeOffset.Now);
            Latest = sample;
        }

        Updated?.Invoke(this, sample);
    }
}

using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Gaming;

/// <summary>
/// Counts presented frames like PresentMon: a real-time event tracing session ("PcChecker-FrameRate") on the DXGI,
/// Direct3D 9 and DirectX kernel present events, filtered to the tracked process. The session is processed on its
/// own background thread; a timer publishes a <see cref="FrameRateSample"/> twice a second. By default the tracked
/// process follows the foreground game reported by <see cref="IForegroundAppTracker"/>.
/// If Windows refuses the session (no admin rights, too many sessions, policy) the monitor reports
/// <see cref="IsAvailable"/> = false with the reason and never throws.
/// </summary>
public sealed class WindowsFrameRateMonitor : IFrameRateMonitor, IDisposable
{
    public const string SessionName = "PcChecker-FrameRate";
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(500);

    // Real-time sessions hand over events about once a second, so "now" trails the wall clock by up to this much.
    private const double DeliveryLatencyMs = 1100;

    private readonly IForegroundAppTracker _foreground;
    private readonly ILogger<WindowsFrameRateMonitor> _logger;
    private readonly object _gate = new();
    private readonly PresentCounter _counter = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private Timer? _publishTimer;
    private Stopwatch? _clock;
    private double _latestEventMs;
    private int? _target;
    private string _targetName = string.Empty;
    private bool _started;

    public WindowsFrameRateMonitor(IForegroundAppTracker foreground, ILogger<WindowsFrameRateMonitor> logger)
    {
        _foreground = foreground;
        _logger = logger;
        IsAvailable = PlatformGuard.IsWindows;
        UnavailableReason = PlatformGuard.IsWindows ? null : "Frame counting needs Windows.";
    }

    public bool IsAvailable { get; private set; }

    public string? UnavailableReason { get; private set; }

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
            if (_target == processId)
            {
                return;
            }

            _target = processId;
            _targetName = processId is { } pid ? ProcessName(pid) : string.Empty;
            _counter.Reset();
            Latest = null;
        }

        _logger.LogDebug("Frame counter now tracks {Process} (pid {Pid}).", _targetName, processId);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _foreground.ForegroundChanged += OnForegroundChanged;
        _foreground.Start();
        if (_foreground.Current is { } current && TargetProcessId is null)
        {
            Track(current.Pid);
        }

        if (!PlatformGuard.IsWindows)
        {
            MarkUnavailable("Frame counting needs Windows.");
            return;
        }

        try
        {
            if (TraceEventSession.IsElevated() != true)
            {
                MarkUnavailable("Frame counting needs administrator rights. Restart PC Checker as administrator.");
                return;
            }

            StopStaleSession();
            var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            try
            {
                session.EnableProvider(PresentEvents.DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue);
                session.EnableProvider(PresentEvents.D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue);
                TryEnableKernelProvider(session);
                session.Source.AllEvents += OnEvent;
            }
            catch
            {
                session.Dispose();
                throw;
            }

            lock (_gate)
            {
                _session = session;
                _clock = Stopwatch.StartNew();
                _latestEventMs = 0;
            }

            _thread = new Thread(() => ProcessEvents(session)) { IsBackground = true, Name = "PcChecker frame counter" };
            _thread.Start();
            _publishTimer = new Timer(_ => Publish(), null, PublishInterval, PublishInterval);
            IsAvailable = true;
            UnavailableReason = null;
            _logger.LogInformation("Frame counting started (event tracing session \"{Session}\").", SessionName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The frame-counting session could not be started.");
            MarkUnavailable("Windows did not allow a frame-counting session: " + ex.Message
                + " Another tool may be using too many tracing sessions, or a policy blocks them.");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
        }

        _foreground.ForegroundChanged -= OnForegroundChanged;
        _foreground.Stop();
        _publishTimer?.Dispose();
        _publishTimer = null;
        TraceEventSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _counter.Reset();
            Latest = null;
        }

        try
        {
            session?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping the frame-counting session failed.");
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _logger.LogInformation("Frame counting stopped.");
    }

    public void Dispose() => Stop();

    private void OnForegroundChanged(object? sender, ForegroundApp app) => Track(app.Pid);

    private void OnEvent(TraceEvent data)
    {
        try
        {
            var source = PresentEvents.Classify(data.ProviderGuid, (int)data.ID);
            double timestamp = data.TimeStampRelativeMSec;
            lock (_gate)
            {
                if (timestamp > _latestEventMs)
                {
                    _latestEventMs = timestamp;
                }

                if (source is { } s && _target == data.ProcessID)
                {
                    _counter.Add(s, timestamp);
                }
            }
        }
        catch
        {
            // A malformed event must not stop the session.
        }
    }

    private void Publish()
    {
        FrameRateSample? sample = null;
        lock (_gate)
        {
            if (_target is not { } pid || _clock is null)
            {
                Latest = null;
                return;
            }

            double now = Math.Max(_latestEventMs, _clock.Elapsed.TotalMilliseconds - DeliveryLatencyMs);
            var snapshot = _counter.Snapshot(now);
            if (snapshot is { } s)
            {
                sample = new FrameRateSample(pid, _targetName, s.Fps, s.FrameTimeMs, s.OnePercentLowFps, DateTimeOffset.Now);
            }

            Latest = sample;
        }

        if (sample is not null)
        {
            try
            {
                Updated?.Invoke(this, sample);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "A frame-rate listener failed.");
            }
        }
    }

    private void ProcessEvents(TraceEventSession session)
    {
        try
        {
            // Blocks until the session is disposed.
            session.Source.Process();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The frame-counting session stopped unexpectedly.");
            bool stillWanted;
            lock (_gate)
            {
                stillWanted = _started && ReferenceEquals(_session, session);
            }

            if (stillWanted)
            {
                MarkUnavailable("The frame-counting session stopped unexpectedly: " + ex.Message);
            }
        }
    }

    private void TryEnableKernelProvider(TraceEventSession session)
    {
        try
        {
            session.EnableProvider(PresentEvents.DxgKrnlProvider, TraceEventLevel.Informational, PresentEvents.DxgKrnlKeywords);
        }
        catch (Exception ex)
        {
            // Only OpenGL and Vulkan games need the kernel events; DirectX games still work.
            _logger.LogInformation(ex, "DirectX kernel present events are not available; OpenGL/Vulkan games will show \"--\".");
        }
    }

    private void StopStaleSession()
    {
        try
        {
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
            {
                TraceEventSession.GetActiveSession(SessionName)?.Stop();
                _logger.LogInformation("Stopped a frame-counting session left over from an earlier run.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check for an old frame-counting session.");
        }
    }

    private void MarkUnavailable(string reason)
    {
        IsAvailable = false;
        UnavailableReason = reason;
        lock (_gate)
        {
            Latest = null;
        }
    }

    private static string ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return GameProfileRules.CanonicalExeName(GamingNativeMethods.TryGetProcessPath(processId)) is { Length: > 0 } exe ? exe : $"process {processId}";
        }
    }
}

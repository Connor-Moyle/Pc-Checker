using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Input;

namespace PcChecker.App.Services;

/// <summary>
/// Measures the mouse report (polling) rate with Raw Input: a hidden window registers for mouse input with
/// <c>RIDEV_INPUTSINK</c> so it receives every <c>WM_INPUT</c> even while another window has focus, and the gaps
/// between reports give the rate. The user has to keep the mouse moving for the whole test.
/// </summary>
public sealed class RawInputPollingMeter : IMousePollingMeter
{
    private readonly ILogger<RawInputPollingMeter> _logger;

    public RawInputPollingMeter(ILogger<RawInputPollingMeter> logger)
    {
        _logger = logger;
    }

    public async Task<int> MeasureAsync(int seconds, IProgress<int>? liveHz, CancellationToken cancellationToken)
    {
        seconds = Math.Clamp(seconds, 1, 60);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _logger.LogWarning("Polling rate measurement needs the UI thread; none is available.");
            return 0;
        }

        Session? session;
        try
        {
            session = await dispatcher.InvokeAsync(() => Session.TryStart(_logger));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not create the Raw Input window for the polling rate test.");
            return 0;
        }

        if (session is null)
        {
            return 0;
        }

        try
        {
            var clock = Stopwatch.StartNew();
            var total = TimeSpan.FromSeconds(seconds);
            while (clock.Elapsed < total)
            {
                await Task.Delay(200, cancellationToken);
                liveHz?.Report(session.LiveHz(clock.Elapsed.TotalMilliseconds));
            }

            int result = session.EstimateHz();
            _logger.LogInformation("Mouse polling rate measured at {Hz} Hz over {Seconds} s ({Reports} reports).", result, seconds, session.ReportCount);
            return result;
        }
        finally
        {
            try
            {
                await dispatcher.InvokeAsync(session.Dispose);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Raw Input window could not be closed cleanly.");
            }
        }
    }

    /// <summary>One measurement: the hidden window, its Raw Input registration and the report timestamps.</summary>
    private sealed class Session : IDisposable
    {
        private const int WmInput = 0x00FF;
        private const int WsPopup = unchecked((int)0x80000000);
        private const int WsExToolWindow = 0x00000080;
        private const uint RidevInputSink = 0x00000100;
        private const uint RidevRemove = 0x00000001;
        private const ushort UsagePageGeneric = 0x01;
        private const ushort UsageMouse = 0x02;

        private readonly object _gate = new();
        private readonly List<double> _timestampsMs = new(16_000);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ILogger _logger;
        private HwndSource? _source;
        private bool _registered;

        private Session(ILogger logger)
        {
            _logger = logger;
        }

        public int ReportCount
        {
            get
            {
                lock (_gate)
                {
                    return _timestampsMs.Count;
                }
            }
        }

        public static Session? TryStart(ILogger logger)
        {
            var session = new Session(logger);
            try
            {
                var parameters = new HwndSourceParameters("PcChecker.RawInputMeter", 0, 0)
                {
                    WindowStyle = WsPopup,
                    ExtendedWindowStyle = WsExToolWindow,
                    PositionX = -32000,
                    PositionY = -32000,
                };
                session._source = new HwndSource(parameters);
                session._source.AddHook(session.WndProc);

                var devices = new[]
                {
                    new RawInputDevice { UsagePage = UsagePageGeneric, Usage = UsageMouse, Flags = RidevInputSink, Target = session._source.Handle },
                };
                if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
                {
                    int error = Marshal.GetLastWin32Error();
                    logger.LogWarning("RegisterRawInputDevices failed (Win32 error {Error}); the polling rate cannot be measured.", error);
                    session.Dispose();
                    return null;
                }

                session._registered = true;
                return session;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Raw Input setup failed.");
                session.Dispose();
                return null;
            }
        }

        /// <summary>Reports received during the last second, scaled to Hz.</summary>
        public int LiveHz(double nowMs)
        {
            lock (_gate)
            {
                return PollingRateMath.CountInWindow(_timestampsMs, nowMs);
            }
        }

        /// <summary>Final estimate from the median gap between reports while the mouse was moving.</summary>
        public int EstimateHz()
        {
            double[] snapshot;
            lock (_gate)
            {
                snapshot = _timestampsMs.ToArray();
            }

            var intervals = new List<double>(Math.Max(0, snapshot.Length - 1));
            for (int i = 1; i < snapshot.Length; i++)
            {
                intervals.Add(snapshot[i] - snapshot[i - 1]);
            }

            int estimate = PollingRateMath.EstimateHz(intervals);
            if (estimate > 0)
            {
                return estimate;
            }

            // Too few reports for the median: fall back to the busiest one-second window.
            int peak = 0;
            foreach (var t in snapshot)
            {
                peak = Math.Max(peak, PollingRateMath.CountInWindow(snapshot, t));
            }

            return peak;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmInput)
            {
                double now = _clock.Elapsed.TotalMilliseconds;
                lock (_gate)
                {
                    _timestampsMs.Add(now);
                }
            }

            // Leave handled = false so DefWindowProc performs the Raw Input cleanup.
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            try
            {
                if (_registered)
                {
                    var devices = new[] { new RawInputDevice { UsagePage = UsagePageGeneric, Usage = UsageMouse, Flags = RidevRemove, Target = IntPtr.Zero } };
                    RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>());
                    _registered = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Raw Input unregister failed.");
            }

            if (_source is not null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    }
}

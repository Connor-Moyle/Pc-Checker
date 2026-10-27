using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;
using PcChecker.Core.Gaming;

namespace PcChecker.App.ViewModels;

/// <summary>Overlay page, part 3: the FPS counter status line, the measured game and why counting is unavailable.</summary>
public sealed partial class OverlayViewModel
{
    private string? _frameGenerationNote;
    private bool _frameGenerationChecked;

    /// <summary>The AMD Fluid Motion Frames note, when AMD Software is installed.</summary>
    public string? FrameGenerationNote
    {
        get
        {
            if (!_frameGenerationChecked)
            {
                _frameGenerationChecked = true;
                try
                {
                    _frameGenerationNote = FrameRateNotes.FrameGeneration(PcChecker.Core.Overclocking.VendorSoftwareCatalog.AmdAdrenalin.Locate(System.IO.File.Exists) is not null);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AMD Software could not be detected for the frame generation note.");
                }
            }

            return _frameGenerationNote;
        }
    }

    private void StartFrameStatus()
    {
        UpdateFrameStatus();
        _frameStatusTimer?.Start();
    }

    private void StopFrameStatus() => _frameStatusTimer?.Stop();

    private void UpdateFrameStatus()
    {
        if (_frames is null)
        {
            return;
        }

        try
        {
            FrameCounterProblem = _frames.IsAvailable ? null : _frames.UnavailableReason ?? "The frame counter is not available on this PC.";
            var sample = _frames.Latest;
            bool wanted = Metrics.Any(m => m.Enabled && OverlayMetricFormatter.IsFrameMetric(m.Id));
            string? target = sample?.ProcessName ?? _tracker?.Current?.ProcessName;
            TrackedGameText = target is { Length: > 0 }
                ? $"FPS for: {GameProfileRules.CanonicalExeName(target)}"
                : "FPS for: waiting for a game";
            FrameRateStatus = !wanted
                ? "Tick FPS, Frame time or 1% low under Metrics to start counting frames."
                : sample is not null
                    ? $"Measuring {sample.ProcessName}: {sample.Fps:0} FPS, {sample.FrameTimeMs:0.0} ms per frame"
                      + (sample.OnePercentLowFps is { } low ? $", 1% low {low:0}." : ".")
                    : IsFrameCounterUnavailable
                        ? "Frame counting is not running."
                        : "Waiting for frames: start or switch to a game. The overlay shows “--” until frames arrive.";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Frame counter status could not be read.");
        }
    }
}

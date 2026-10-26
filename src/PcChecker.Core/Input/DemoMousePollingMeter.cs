namespace PcChecker.Core.Input;

/// <summary>Demo polling meter: ramps a live reading up to a steady 1000 Hz gaming-mouse rate. Pure .NET.</summary>
public sealed class DemoMousePollingMeter : IMousePollingMeter
{
    public const int DemoHz = 1000;

    public async Task<int> MeasureAsync(int seconds, IProgress<int>? liveHz, CancellationToken cancellationToken)
    {
        seconds = Math.Clamp(seconds, 1, 60);
        var random = Random.Shared;
        int ticks = seconds * 5;
        for (int i = 1; i <= ticks; i++)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            // The first half second "spins up" as the user starts moving, then it jitters around the nominal rate.
            int value = i <= 2 ? DemoHz * i / 3 : DemoHz + random.Next(-12, 9);
            liveHz?.Report(value);
        }

        return DemoHz - 3;
    }
}

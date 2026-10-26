namespace PcChecker.Core.Audio;

/// <summary>
/// Cheap, frequent read of the live peak level of every active device, keyed by device id. The page polls this on a
/// short timer while it is visible instead of re-enumerating every device property each tick.
/// </summary>
public interface IAudioLevelMeter
{
    Task<IReadOnlyDictionary<string, float>> ReadPeakLevelsAsync(CancellationToken cancellationToken);
}

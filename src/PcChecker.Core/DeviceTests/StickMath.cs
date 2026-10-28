namespace PcChecker.Core.DeviceTests;

/// <summary>Result of the drift check: where the stick rests when nobody touches it.</summary>
public enum DriftVerdict
{
    /// <summary>Too few readings to judge (the controller disconnected or the check was stopped).</summary>
    NotEnoughData,

    /// <summary>The stick rests in the middle.</summary>
    Centred,

    /// <summary>A small offset that games' dead zones normally hide.</summary>
    SlightDrift,

    /// <summary>The offset is bigger than a typical dead zone: games may move on their own.</summary>
    Drift,

    /// <summary>The stick moved during the check, so it says nothing about drift.</summary>
    Moved,
}

/// <summary>Drift check numbers. Offsets are in stick units (1 = pushed all the way).</summary>
public sealed record DriftResult(DriftVerdict Verdict, double OffsetX, double OffsetY, double Offset, double Spread, int Samples)
{
    public double OffsetPercent => Offset * 100;
}

/// <summary>Result of the roundness check.</summary>
public enum CircularityVerdict
{
    /// <summary>Not enough of the edge was covered yet.</summary>
    KeepSpinning,
    Good,
    Fair,
    Poor,
}

/// <summary>Roundness numbers: how far the stick's outer edge is from a perfect circle.</summary>
public sealed record CircularityResult(
    CircularityVerdict Verdict,
    double CoveragePercent,
    double AverageErrorPercent,
    int SectorsFilled,
    IReadOnlyList<double> SectorRadii);

/// <summary>Pure maths for the controller test: stick drift, stick roundness and value conversions.</summary>
public static class StickMath
{
    /// <summary>Dead zone most games use for sticks (10 % of the travel).</summary>
    public const double DefaultDeadzone = 0.10;

    /// <summary>Resting offsets below this count as centred.</summary>
    public const double CentredLimit = 0.04;

    /// <summary>A spread larger than this during the drift check means the stick was touched.</summary>
    public const double MovedLimit = 0.20;

    /// <summary>Fewest readings the drift check needs (2 seconds at 60 readings a second gives 120).</summary>
    public const int MinimumDriftSamples = 20;

    /// <summary>Number of angle slices the roundness check fills.</summary>
    public const int DefaultSectors = 36;

    /// <summary>Only readings at least this far out count towards the outer edge.</summary>
    public const double EdgeThreshold = 0.5;

    /// <summary>Share of the slices that must be filled before roundness is judged.</summary>
    public const double RequiredCoveragePercent = 90;

    /// <summary>
    /// Judges drift from readings taken while the stick is left alone: the average position is the resting offset and
    /// the largest distance from that average shows whether the stick was touched.
    /// </summary>
    public static DriftResult AnalyzeDrift(IReadOnlyList<StickPoint> samples, double deadzone = DefaultDeadzone)
    {
        if (samples is null || samples.Count < MinimumDriftSamples)
        {
            return new DriftResult(DriftVerdict.NotEnoughData, 0, 0, 0, 0, samples?.Count ?? 0);
        }

        double meanX = samples.Average(s => s.X);
        double meanY = samples.Average(s => s.Y);
        double offset = Math.Sqrt(meanX * meanX + meanY * meanY);
        double spread = samples.Max(s => Math.Sqrt((s.X - meanX) * (s.X - meanX) + (s.Y - meanY) * (s.Y - meanY)));

        var verdict = spread > MovedLimit ? DriftVerdict.Moved
            : offset >= deadzone ? DriftVerdict.Drift
            : offset >= CentredLimit ? DriftVerdict.SlightDrift
            : DriftVerdict.Centred;
        return new DriftResult(verdict, meanX, meanY, offset, spread, samples.Count);
    }

    /// <summary>
    /// Judges roundness from readings taken while the stick is spun around its edge. The circle is cut into
    /// <paramref name="sectors"/> slices; each slice keeps the furthest reading in it, and the average distance of those
    /// from 1 (a perfect circle) is the error. Readings closer to the middle than <see cref="EdgeThreshold"/> are ignored.
    /// </summary>
    public static CircularityResult AnalyzeCircularity(IEnumerable<StickPoint> samples, int sectors = DefaultSectors)
    {
        sectors = Math.Max(4, sectors);
        var radii = new double[sectors];
        foreach (var sample in samples ?? [])
        {
            double r = sample.Magnitude;
            if (r < EdgeThreshold || double.IsNaN(r))
            {
                continue;
            }

            int sector = SectorOf(sample, sectors);
            radii[sector] = Math.Max(radii[sector], r);
        }

        var filled = radii.Where(r => r > 0).ToList();
        double coverage = filled.Count * 100.0 / sectors;
        double error = filled.Count == 0 ? 0 : filled.Average(r => Math.Abs(r - 1)) * 100;
        var verdict = coverage < RequiredCoveragePercent ? CircularityVerdict.KeepSpinning
            : error < 10 ? CircularityVerdict.Good
            : error < 20 ? CircularityVerdict.Fair
            : CircularityVerdict.Poor;
        return new CircularityResult(verdict, coverage, error, filled.Count, radii);
    }

    /// <summary>The slice a reading falls into, counting anticlockwise from the right.</summary>
    public static int SectorOf(StickPoint point, int sectors)
    {
        double angle = Math.Atan2(point.Y, point.X);
        if (angle < 0)
        {
            angle += 2 * Math.PI;
        }

        int sector = (int)(angle / (2 * Math.PI) * sectors);
        return Math.Clamp(sector, 0, sectors - 1);
    }

    /// <summary>XInput thumbstick value (-32768..32767) to -1..1.</summary>
    public static double FromXInputAxis(short value) => value < 0 ? value / 32768.0 : value / 32767.0;

    /// <summary>XInput trigger value (0..255) to 0..1.</summary>
    public static double FromXInputTrigger(byte value) => value / 255.0;

    /// <summary>A generic controller axis (0..1, 0.5 in the middle) to -1..1; <paramref name="invert"/> flips it (HID Y grows downwards).</summary>
    public static double FromRawAxis(double value, bool invert = false)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        double centred = Math.Clamp(value, 0, 1) * 2 - 1;
        return invert ? -centred : centred;
    }
}

namespace PcChecker.Core.Common;

/// <summary>Helpers for code paths that only work on Windows.</summary>
public static class PlatformGuard
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Throws <see cref="PlatformNotSupportedException"/> with a friendly message when not on Windows.</summary>
    public static void ThrowIfNotWindows(string feature)
    {
        if (!IsWindows)
        {
            throw new PlatformNotSupportedException($"{feature} is only available on Windows.");
        }
    }
}

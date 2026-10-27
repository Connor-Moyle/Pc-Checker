namespace PcChecker.Core.Gaming;

/// <summary>Plain-English notes about what the FPS counter can and cannot see. Pure.</summary>
public static class FrameRateNotes
{
    /// <summary>
    /// AMD Fluid Motion Frames inserts generated frames in the driver, after the game hands its frames to Windows,
    /// so the counter (like PresentMon) shows the frames the game rendered: about half of what the screen shows
    /// with AFMF on. Null when AMD Software is not installed.
    /// </summary>
    public static string? FrameGeneration(bool amdSoftwareInstalled) => amdSoftwareInstalled
        ? "AMD Fluid Motion Frames (AFMF) adds frames inside the driver, after the game. They are not counted: the number shows the frames the game rendered, about half of what you see with AFMF on."
        : null;
}

namespace PcChecker.Core.Gaming;

/// <summary>Where a present event came from.</summary>
public enum PresentSource
{
    /// <summary>The graphics runtime (DXGI for Direct3D 10/11/12, or Direct3D 9): one event per frame the game presents.</summary>
    Runtime,

    /// <summary>The DirectX graphics kernel: also covers OpenGL and Vulkan games that bypass the runtime events.</summary>
    Kernel,
}

/// <summary>
/// The event tracing providers and event ids that mark "a frame was presented" (the same ones PresentMon uses).
/// Pure lookup so the classification is testable without a trace session.
/// </summary>
public static class PresentEvents
{
    public const string DxgiProviderName = "Microsoft-Windows-DXGI";
    public const string D3D9ProviderName = "Microsoft-Windows-D3D9";
    public const string DxgKrnlProviderName = "Microsoft-Windows-DxgKrnl";

    public static readonly Guid DxgiProvider = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
    public static readonly Guid D3D9Provider = new("783aca0a-790e-4d7f-8451-aa850511c6b9");
    public static readonly Guid DxgKrnlProvider = new("802ec45a-1e99-4b83-9920-87c98277ba9d");

    /// <summary>DXGI Present_Start.</summary>
    public const int DxgiPresentStart = 42;

    /// <summary>DXGI PresentMultiplaneOverlay_Start.</summary>
    public const int DxgiPresentMultiplaneOverlayStart = 55;

    /// <summary>D3D9 Present_Start.</summary>
    public const int D3D9PresentStart = 1;

    /// <summary>DxgKrnl Present_Info (one per present submitted to the kernel, any graphics API).</summary>
    public const int DxgKrnlPresentInfo = 184;

    /// <summary>DxgKrnl keywords enabled for the kernel fallback: Base (0x1), Present (0x8000000) and the Performance channel bit.</summary>
    public const ulong DxgKrnlKeywords = 0x1UL | 0x8000000UL | 0x8000000000000000UL;

    /// <summary>Returns the source when the event is a present, or null for every other event.</summary>
    public static PresentSource? Classify(Guid provider, int eventId)
    {
        if (provider == DxgiProvider)
        {
            return eventId is DxgiPresentStart or DxgiPresentMultiplaneOverlayStart ? PresentSource.Runtime : null;
        }

        if (provider == D3D9Provider)
        {
            return eventId == D3D9PresentStart ? PresentSource.Runtime : null;
        }

        if (provider == DxgKrnlProvider)
        {
            return eventId == DxgKrnlPresentInfo ? PresentSource.Kernel : null;
        }

        return null;
    }
}

/// <summary>
/// Keeps separate frame statistics for runtime and kernel presents of one process and reports the runtime ones when
/// they are flowing (so a DirectX game is not counted twice), falling back to the kernel ones for OpenGL/Vulkan.
/// Not thread-safe: the caller locks.
/// </summary>
public sealed class PresentCounter
{
    private readonly FrameStats _runtime = new();
    private readonly FrameStats _kernel = new();

    public void Add(PresentSource source, double timestampMs)
    {
        (source == PresentSource.Runtime ? _runtime : _kernel).Add(timestampMs);
    }

    public void Reset()
    {
        _runtime.Reset();
        _kernel.Reset();
    }

    /// <summary>Which source the next snapshot uses, or null when neither has recent frames.</summary>
    public PresentSource? ActiveSource(double nowMs) =>
        _runtime.HasRecentFrames(nowMs) ? PresentSource.Runtime
        : _kernel.HasRecentFrames(nowMs) ? PresentSource.Kernel
        : null;

    public FrameStatsSnapshot? Snapshot(double nowMs) => ActiveSource(nowMs) switch
    {
        PresentSource.Runtime => _runtime.Snapshot(nowMs),
        PresentSource.Kernel => _kernel.Snapshot(nowMs),
        _ => null,
    };
}

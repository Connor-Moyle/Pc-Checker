namespace PcChecker.Core.Overclocking.GpuLoad;

/// <summary>Pure classification of the DXGI errors that mean the graphics driver reset under load.</summary>
public static class GpuDeviceLoss
{
    public const int DeviceRemoved = unchecked((int)0x887A0005);
    public const int DeviceHung = unchecked((int)0x887A0006);
    public const int DeviceReset = unchecked((int)0x887A0007);
    public const int DriverInternalError = unchecked((int)0x887A0020);

    public const string DriverResetMessage = "The graphics driver reset — the GPU is not stable at these settings";

    /// <summary>True for device removed, hung, reset and driver-internal errors.</summary>
    public static bool IsDeviceLost(int hresult) =>
        hresult is DeviceRemoved or DeviceHung or DeviceReset or DriverInternalError;

    /// <summary>Plain-English reason for a lost device, with the raw code for support.</summary>
    public static string Describe(int hresult)
    {
        string what = hresult switch
        {
            DeviceHung => "the GPU stopped responding",
            DeviceReset => "the GPU was reset",
            DriverInternalError => "the driver hit an internal error",
            _ => "the GPU was removed from Windows",
        };
        return $"{DriverResetMessage} ({what}, 0x{hresult:X8}).";
    }
}

/// <summary>Thrown inside the generator when the device is lost; converted into a failed <see cref="GpuLoadResult"/>.</summary>
public sealed class GpuDeviceLostException : Exception
{
    public GpuDeviceLostException(int hresult)
        : base(GpuDeviceLoss.Describe(hresult))
    {
        HResult = hresult;
    }
}

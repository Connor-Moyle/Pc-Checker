using System.Runtime.InteropServices;

namespace PcChecker.Core.Audio;

/// <summary>Core Audio endpoint roles (the ERole enumeration).</summary>
internal enum PolicyRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

/// <summary>
/// The undocumented but long-stable <c>IPolicyConfig</c> interface Windows uses to change the default playback
/// and recording device. Vtable order follows the public reverse-engineered definition used by every
/// "set default device" tool.
/// </summary>
[ComImport]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PolicyRole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
}

/// <summary>Older (Vista-era) layout of the same interface, without <c>ResetDeviceFormat</c>. Used as a fallback.</summary>
[ComImport]
[Guid("568b9108-44bf-40b4-9006-86afe5b5a620")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigVista
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out IntPtr format);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PolicyRole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
}

/// <summary>The COM class that implements <see cref="IPolicyConfig"/> (CLSID_PolicyConfigClient).</summary>
[ComImport]
[Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient
{
}

/// <summary>Thin wrapper around <see cref="IPolicyConfig"/> that tries the current layout first, then the Vista one.</summary>
internal static class PolicyConfig
{
    /// <summary>Sets <paramref name="deviceId"/> as the default endpoint for every role in <paramref name="roles"/>.</summary>
    /// <returns>Null on success, or an error message.</returns>
    public static string? SetDefaultEndpoint(string deviceId, IEnumerable<PolicyRole> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        object client;
        try
        {
            client = new PolicyConfigClient();
        }
        catch (Exception ex)
        {
            return "The Windows audio policy service could not be reached: " + ex.Message;
        }

        try
        {
            foreach (var role in roles)
            {
                int hr = client switch
                {
                    IPolicyConfig modern => modern.SetDefaultEndpoint(deviceId, role),
                    IPolicyConfigVista vista => vista.SetDefaultEndpoint(deviceId, role),
                    _ => unchecked((int)0x80004002), // E_NOINTERFACE
                };
                if (hr != 0)
                {
                    return $"Windows refused to change the default device for role {role} (HRESULT 0x{hr:X8}).";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return "Changing the default device failed: " + ex.Message;
        }
        finally
        {
            try
            {
                Marshal.FinalReleaseComObject(client);
            }
            catch
            {
                // Releasing is best effort.
            }
        }
    }
}

using System.Runtime.InteropServices;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Settings;

/// <summary>
/// Protects secrets with the Windows Data Protection API (DPAPI) scoped to the current user.
/// Throws <see cref="PlatformNotSupportedException"/> on other operating systems.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PcChecker.SecretStore.v1");

    public string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        PlatformGuard.ThrowIfNotWindows("Secret protection");
        var data = Encoding.UTF8.GetBytes(plain);
        var protectedBytes = NativeDpapi.Protect(data, Entropy);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedBase64)
    {
        ArgumentNullException.ThrowIfNull(protectedBase64);
        PlatformGuard.ThrowIfNotWindows("Secret protection");
        var data = Convert.FromBase64String(protectedBase64);
        var plain = NativeDpapi.Unprotect(data, Entropy);
        return Encoding.UTF8.GetString(plain);
    }

    private static class NativeDpapi
    {
        private const int CryptProtectUiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        public static byte[] Protect(byte[] data, byte[] entropy) => Call(data, entropy, protect: true);

        public static byte[] Unprotect(byte[] data, byte[] entropy) => Call(data, entropy, protect: false);

        private static byte[] Call(byte[] data, byte[] entropy, bool protect)
        {
            var inPtr = Marshal.AllocHGlobal(Math.Max(1, data.Length));
            var entropyPtr = Marshal.AllocHGlobal(Math.Max(1, entropy.Length));
            try
            {
                Marshal.Copy(data, 0, inPtr, data.Length);
                Marshal.Copy(entropy, 0, entropyPtr, entropy.Length);
                var input = new DataBlob { Size = data.Length, Data = inPtr };
                var entropyBlob = new DataBlob { Size = entropy.Length, Data = entropyPtr };

                bool ok = protect
                    ? CryptProtectData(ref input, "PcChecker", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
                if (!ok)
                {
                    throw new InvalidOperationException("DPAPI call failed.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                }

                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inPtr);
                Marshal.FreeHGlobal(entropyPtr);
            }
        }
    }
}

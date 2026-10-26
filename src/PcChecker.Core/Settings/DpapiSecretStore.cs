using System.Security.Cryptography;
using System.Text;
using PcChecker.Core.Common;

namespace PcChecker.Core.Settings;

/// <summary>
/// Protects secrets (such as the Claude API key) with the Windows Data Protection API (DPAPI) via
/// <see cref="ProtectedData"/>, scoped to the current Windows user. The protected value can only be
/// read back by the same user on the same machine. Throws <see cref="PlatformNotSupportedException"/>
/// on other operating systems.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    /// <summary>Extra entropy mixed into every blob so values protected by other apps cannot be swapped in.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PcChecker.SecretStore.v1");

    public string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        PlatformGuard.ThrowIfNotWindows("Secret protection");
        var data = Encoding.UTF8.GetBytes(plain);
        var protectedBytes = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedBase64)
    {
        ArgumentNullException.ThrowIfNull(protectedBase64);
        PlatformGuard.ThrowIfNotWindows("Secret protection");
        var data = Convert.FromBase64String(protectedBase64);
        var plain = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}

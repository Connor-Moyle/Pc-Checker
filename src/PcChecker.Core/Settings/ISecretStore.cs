namespace PcChecker.Core.Settings;

/// <summary>Protects small secrets (API keys) for the current Windows user.</summary>
public interface ISecretStore
{
    /// <summary>Encrypts <paramref name="plain"/> and returns base64 text safe to store in settings.</summary>
    string Protect(string plain);

    /// <summary>Decrypts text produced by <see cref="Protect"/>.</summary>
    string Unprotect(string protectedBase64);
}

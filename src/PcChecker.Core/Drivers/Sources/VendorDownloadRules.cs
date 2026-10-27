using System.Net;
using System.Text;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>Outcome of checking a downloaded installer's signature.</summary>
public sealed record SignatureVerdict(bool Trusted, string Message);

/// <summary>
/// The safety rules for vendor packages: which HTTPS hosts a package may come from (every redirect is checked too),
/// who must have signed it, how it is started and how its exit code is read. Pure, so every rule is tested.
/// </summary>
public static class VendorDownloadRules
{
    /// <summary>Largest package accepted (NVIDIA's are about 1 GB).</summary>
    public const long MaxPackageBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>Most redirects followed for one download.</summary>
    public const int MaxRedirects = 5;

    /// <summary>The download hosts per vendor. A host matches itself and its subdomains, nothing else.</summary>
    public static IReadOnlyList<string> AllowedHosts(DriverSourceKind source) => source switch
    {
        DriverSourceKind.Nvidia => ["download.nvidia.com"],
        DriverSourceKind.Amd => ["drivers.amd.com"],
        DriverSourceKind.Intel => ["downloadmirror.intel.com", "downloadcenter.intel.com"],
        _ => [],
    };

    /// <summary>The hosts the lookups themselves talk to.</summary>
    public static IReadOnlyList<string> LookupHosts { get; } = [NvidiaDriverLookup.Host, "www.amd.com"];

    /// <summary>
    /// True when <paramref name="url"/> is an absolute HTTPS URL on the default port, without user info, whose host is
    /// one of <paramref name="allowedHosts"/> or a subdomain of one. "download.nvidia.com.example.org",
    /// "evildownload.nvidia.com" style look-alikes, IP addresses and http:// are refused.
    /// </summary>
    public static bool IsAllowed(string? url, IReadOnlyList<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IsAllowed(uri, allowedHosts);
    }

    /// <inheritdoc cref="IsAllowed(string?, IReadOnlyList{string})"/>
    public static bool IsAllowed(Uri uri, IReadOnlyList<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.HostNameType != UriHostNameType.Dns || IPAddress.TryParse(uri.Host, out _))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        foreach (var allowed in allowedHosts)
        {
            var a = allowed.Trim().TrimEnd('.').ToLowerInvariant();
            if (a.Length > 0 && (host == a || host.EndsWith("." + a, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The installer file name from a download URL; only plain .exe names are accepted.</summary>
    public static string? InstallerFileName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.Length > 160
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal) || name.StartsWith('.'))
        {
            return null;
        }

        return name;
    }

    /// <summary>The signer names accepted for each vendor (the certificate subject's CN or O).</summary>
    public static IReadOnlyList<string> ExpectedSigners(DriverSourceKind source) => source switch
    {
        DriverSourceKind.Nvidia => ["NVIDIA Corporation"],
        DriverSourceKind.Amd => ["Advanced Micro Devices, Inc.", "Advanced Micro Devices INC.", "Advanced Micro Devices Inc"],
        DriverSourceKind.Intel => ["Intel Corporation"],
        _ => [],
    };

    /// <summary>The signer named in the confirmation text.</summary>
    public static string ExpectedSignerText(DriverSourceKind source) => ExpectedSigners(source).FirstOrDefault() ?? "the vendor";

    /// <summary>
    /// True when the certificate subject's common name (CN) or organisation (O) is exactly one of the expected names
    /// for <paramref name="source"/>, ignoring case, commas, full stops and repeated spaces. A name that merely
    /// contains the vendor ("NVIDIA Corporation Tools Ltd") does not match.
    /// </summary>
    public static bool SignerMatches(DriverSourceKind source, string? subject) => SignerMatches(ExpectedSigners(source), subject);

    /// <summary>
    /// True when the certificate subject's common name (CN) or organisation (O) is exactly one of
    /// <paramref name="expectedSigners"/>, with the same normalisation as for the driver vendors.
    /// </summary>
    public static bool SignerMatches(IReadOnlyList<string> expectedSigners, string? subject)
    {
        ArgumentNullException.ThrowIfNull(expectedSigners);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        var expected = expectedSigners.Select(NormalizeName).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (expected.Count == 0)
        {
            return false;
        }

        foreach (var (key, value) in DistinguishedNameParts(subject))
        {
            if ((key is "CN" or "O") && expected.Contains(NormalizeName(value)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The decision before a downloaded installer may run: WinVerifyTrust must succeed (valid signature, trusted chain,
    /// not revoked) and the signer must be the vendor. <paramref name="trustResult"/> is the HRESULT.
    /// </summary>
    public static SignatureVerdict Evaluate(DriverSourceKind source, int trustResult, string? signerSubject) =>
        Evaluate(ExpectedSigners(source), trustResult, signerSubject);

    /// <summary>The same decision for any list of accepted signer names; the first name is the one shown in messages.</summary>
    public static SignatureVerdict Evaluate(IReadOnlyList<string> expectedSigners, int trustResult, string? signerSubject)
    {
        ArgumentNullException.ThrowIfNull(expectedSigners);
        string expectedText = expectedSigners.FirstOrDefault() ?? "the vendor";
        if (trustResult == Processes.SignatureRules.TrustENoSignature)
        {
            return new SignatureVerdict(false, "The file is not signed.");
        }

        if (trustResult != Processes.SignatureRules.Success)
        {
            return new SignatureVerdict(false, $"Windows did not accept the file's signature (0x{trustResult:X8}): {DescribeTrustResult(trustResult)}");
        }

        if (!SignerMatches(expectedSigners, signerSubject))
        {
            var who = Processes.SignatureRules.CommonName(signerSubject) ?? "an unknown signer";
            return new SignatureVerdict(false, $"The file is signed by {who}, not by {expectedText}.");
        }

        return new SignatureVerdict(true, $"Signed by {expectedText}; the signature and certificate chain are valid.");
    }

    /// <summary>Plain words for the common WinVerifyTrust failures.</summary>
    public static string DescribeTrustResult(int hresult) => hresult switch
    {
        Processes.SignatureRules.TrustEBadDigest => "the file was changed after it was signed.",
        Processes.SignatureRules.CertEUntrustedRoot => "the certificate chain ends in a root Windows does not trust.",
        Processes.SignatureRules.CertEChaining => "the certificate chain could not be built.",
        Processes.SignatureRules.CertERevoked => "the signing certificate was revoked.",
        Processes.SignatureRules.TrustEExplicitDistrust => "the certificate is marked as not trusted.",
        unchecked((int)0x800B0101) => "the signing certificate has expired.",
        unchecked((int)0x80092013) => "the revocation check could not be completed.",
        _ => "the signature is not valid.",
    };

    /// <summary>
    /// Installer arguments. NVIDIA documents <c>-s</c> (silent), <c>-clean</c> (clean install) and <c>-noreboot</c>;
    /// every other vendor package runs interactively without arguments.
    /// </summary>
    public static IReadOnlyList<string> InstallerArguments(DriverSourceKind source, NvidiaInstallMode mode) =>
        source == DriverSourceKind.Nvidia
            ? mode switch
            {
                NvidiaInstallMode.Silent => ["-s", "-noreboot"],
                NvidiaInstallMode.SilentClean => ["-s", "-clean", "-noreboot"],
                _ => [],
            }
            : [];

    /// <summary>True when the installer runs without its own window, so the app shows the progress.</summary>
    public static bool IsSilent(DriverSourceKind source, NvidiaInstallMode mode) => InstallerArguments(source, mode).Count > 0;

    /// <summary>Reads an installer exit code: 0 success, 3010/1641 success with a restart, anything else a failure.</summary>
    public static (bool Success, bool RebootRequired, string Text) InterpretExitCode(int exitCode, bool interactive) => exitCode switch
    {
        0 => (true, false, "The installer finished."),
        3010 or 1641 => (true, true, "The installer finished. Restart Windows to complete it."),
        _ => (false, false, interactive
            ? $"The installer closed with code {exitCode}. It may have been cancelled, or the driver did not install."
            : $"The installer failed with code {exitCode}."),
    };

    private static string NormalizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool space = false;
        foreach (char c in name.Trim().Trim('"'))
        {
            if (c is ',' or '.')
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>Splits "CN=…, O=\"Advanced Micro Devices, Inc.\", L=…" into key/value pairs, honouring quotes.</summary>
    public static IEnumerable<(string Key, string Value)> DistinguishedNameParts(string subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        int start = 0;
        bool quoted = false;
        for (int i = 0; i <= subject.Length; i++)
        {
            if (i < subject.Length)
            {
                char c = subject[i];
                if (c == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (c != ',' || quoted)
                {
                    continue;
                }
            }

            var part = subject[start..i].Trim();
            start = i + 1;
            int eq = part.IndexOf('=');
            if (eq > 0)
            {
                yield return (part[..eq].Trim().ToUpperInvariant(), part[(eq + 1)..].Trim().Trim('"'));
            }
        }
    }
}

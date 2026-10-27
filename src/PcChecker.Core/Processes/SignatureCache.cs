namespace PcChecker.Core.Processes;

/// <summary>Maps WinVerifyTrust results to <see cref="SignatureState"/>.</summary>
public static class SignatureRules
{
    public const int Success = 0;
    public const int TrustENoSignature = unchecked((int)0x800B0100);
    public const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    public const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    public const int TrustEActionUnknown = unchecked((int)0x800B0002);
    public const int CryptEFileError = unchecked((int)0x80092003);
    public const int TrustEBadDigest = unchecked((int)0x80096010);
    public const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
    public const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    public const int CertEChaining = unchecked((int)0x800B010A);
    public const int CertERevoked = unchecked((int)0x800B010C);

    /// <summary>
    /// Signed for success; Unsigned when there is no signature (the caller then tries the Windows catalogs); Unknown when
    /// the file could not be read or the format is not signable; Invalid for every other failure (bad digest, untrusted
    /// or distrusted certificate, revoked).
    /// </summary>
    public static SignatureState Classify(int hresult) => hresult switch
    {
        Success => SignatureState.Signed,
        TrustENoSignature => SignatureState.Unsigned,
        TrustESubjectFormUnknown or TrustEProviderUnknown or TrustEActionUnknown or CryptEFileError => SignatureState.Unknown,
        _ => SignatureState.Invalid,
    };

    /// <summary>Signer common name from a certificate subject: "CN=Valve Corp., O=Valve Corp., L=Bellevue" → "Valve Corp.".</summary>
    public static string? CommonName(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        foreach (string part in SplitDistinguishedName(subject))
        {
            string trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                string value = trimmed[3..].Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitDistinguishedName(string subject)
    {
        int start = 0;
        bool quoted = false;
        for (int i = 0; i < subject.Length; i++)
        {
            char c = subject[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                yield return subject[start..i];
                start = i + 1;
            }
        }

        yield return subject[start..];
    }
}

/// <summary>
/// Remembers signature checks per file path so each program file is checked once per session. Checks are slow (tens
/// of milliseconds), so <see cref="CheckPending"/> runs at most a few per refresh and the rest show as pending.
/// Thread-safe.
/// </summary>
public sealed class SignatureCache
{
    private readonly Func<string, SignatureInfo> _verify;
    private readonly Dictionary<string, SignatureInfo> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public SignatureCache(Func<string, SignatureInfo> verify)
    {
        _verify = verify ?? throw new ArgumentNullException(nameof(verify));
    }

    /// <summary>Number of files checked so far.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _results.Count;
            }
        }
    }

    /// <summary>The cached result, <see cref="SignatureInfo.Pending"/> when not checked yet, or Unknown without a path.</summary>
    public SignatureInfo Get(string? path)
    {
        if (!ProcessSuspicionRules.IsFullPath(path))
        {
            return SignatureInfo.Unknown;
        }

        lock (_gate)
        {
            return _results.TryGetValue(path, out var info) ? info : SignatureInfo.Pending;
        }
    }

    /// <summary>
    /// Checks up to <paramref name="maxChecks"/> paths that have no result yet, in the order given (put the busiest
    /// processes first). Returns how many were checked. A verifier that throws records Unknown.
    /// </summary>
    public int CheckPending(IEnumerable<string?> paths, int maxChecks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        int checkedCount = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in paths)
        {
            if (checkedCount >= maxChecks || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!ProcessSuspicionRules.IsFullPath(path) || !seen.Add(path))
            {
                continue;
            }

            lock (_gate)
            {
                if (_results.ContainsKey(path))
                {
                    continue;
                }
            }

            SignatureInfo result;
            try
            {
                result = _verify(path);
            }
            catch
            {
                result = SignatureInfo.Unknown;
            }

            lock (_gate)
            {
                _results[path] = result;
            }

            checkedCount++;
        }

        return checkedCount;
    }
}

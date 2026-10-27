using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace PcChecker.Core.Processes;

/// <summary>
/// Authenticode check of a program file with WinVerifyTrust: the file's own signature first, then the Windows
/// catalogs (most Windows programs are catalog-signed). Offline only: no revocation check and no network fetches, so
/// a check never waits on the internet. Never throws.
/// </summary>
internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdChoiceCatalog = 2;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x00000010;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x00000080;

    /// <summary>Checks <paramref name="path"/> and names the signer.</summary>
    public static SignatureInfo Verify(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return SignatureInfo.Unknown;
            }

            int embedded = VerifyEmbedded(path);
            var state = SignatureRules.Classify(embedded);
            if (state == SignatureState.Signed)
            {
                return new SignatureInfo(SignatureState.Signed, SignerName(path) ?? CompanyName(path));
            }

            if (state is SignatureState.Unsigned or SignatureState.Unknown && VerifyCatalog(path) is { } catalog)
            {
                return new SignatureInfo(SignatureState.Signed, SignerName(catalog) ?? CompanyName(path));
            }

            return new SignatureInfo(state, state == SignatureState.Invalid ? SignerName(path) : null);
        }
        catch
        {
            return SignatureInfo.Unknown;
        }
    }

    /// <summary>
    /// Strict check of a downloaded installer before it runs: the file's own (embedded) signature only, never a
    /// catalog; the whole certificate chain is checked for revocation online. Returns the WinVerifyTrust HRESULT and
    /// the signing certificate's subject (null when there is none). Never throws.
    /// </summary>
    public static (int Result, string? Subject) VerifyDownloadedInstaller(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (SignatureRules.CryptEFileError, null);
            }

            int result = VerifyEmbedded(path, strict: true);
            string? subject = null;
            try
            {
                using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                subject = certificate.Subject;
            }
            catch (CryptographicException)
            {
                // No signature: the result already says so.
            }

            return (result, subject);
        }
        catch (Exception)
        {
            return (SignatureRules.CryptEFileError, null);
        }
    }

    private static int VerifyEmbedded(string path, bool strict = false)
    {
        IntPtr filePath = Marshal.StringToHGlobalUni(path);
        IntPtr fileInfo = IntPtr.Zero;
        try
        {
            var info = new WintrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = filePath,
            };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(info, fileInfo, false);
            return strict
                ? Run(WtdChoiceFile, fileInfo, WtdRevokeWholeChain, WtdRevocationCheckChainExcludeRoot)
                : Run(WtdChoiceFile, fileInfo);
        }
        finally
        {
            if (fileInfo != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfo);
            }

            Marshal.FreeHGlobal(filePath);
        }
    }

    /// <summary>Path of the trusted catalog that lists the file's hash, or null.</summary>
    private static string? VerifyCatalog(string path)
    {
        // Current catalogs use SHA-256; older ones SHA-1 (null algorithm).
        foreach (string? algorithm in new[] { "SHA256", null })
        {
            if (VerifyCatalog(path, algorithm) is { } catalog)
            {
                return catalog;
            }
        }

        return null;
    }

    private static string? VerifyCatalog(string path, string? algorithm)
    {
        IntPtr admin = IntPtr.Zero;
        IntPtr catalogContext = IntPtr.Zero;
        try
        {
            if (!CryptCATAdminAcquireContext2(out admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0))
            {
                return null;
            }

            byte[] hash;
            using (var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                uint size = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0);
                if (size == 0 || size > 1024)
                {
                    return null;
                }

                hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash, 0))
                {
                    return null;
                }
            }

            catalogContext = CryptCATAdminEnumCatalogFromHash(admin, hash, (uint)hash.Length, 0, IntPtr.Zero);
            if (catalogContext == IntPtr.Zero)
            {
                return null;
            }

            var catalogInfo = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>() };
            if (!CryptCATCatalogInfoFromContext(catalogContext, ref catalogInfo, 0) || string.IsNullOrEmpty(catalogInfo.wszCatalogFile))
            {
                return null;
            }

            return VerifyCatalogMember(path, catalogInfo.wszCatalogFile, hash, admin) == SignatureRules.Success ? catalogInfo.wszCatalogFile : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (catalogContext != IntPtr.Zero)
            {
                CryptCATAdminReleaseCatalogContext(admin, catalogContext, 0);
            }

            if (admin != IntPtr.Zero)
            {
                CryptCATAdminReleaseContext(admin, 0);
            }
        }
    }

    private static int VerifyCatalogMember(string path, string catalogFile, byte[] hash, IntPtr admin)
    {
        IntPtr catalogPath = Marshal.StringToHGlobalUni(catalogFile);
        IntPtr memberTag = Marshal.StringToHGlobalUni(Convert.ToHexString(hash));
        IntPtr memberPath = Marshal.StringToHGlobalUni(path);
        IntPtr hashBuffer = Marshal.AllocHGlobal(hash.Length);
        IntPtr catalogInfo = IntPtr.Zero;
        try
        {
            Marshal.Copy(hash, 0, hashBuffer, hash.Length);
            var info = new WintrustCatalogInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustCatalogInfo>(),
                pcwszCatalogFilePath = catalogPath,
                pcwszMemberTag = memberTag,
                pcwszMemberFilePath = memberPath,
                pbCalculatedFileHash = hashBuffer,
                cbCalculatedFileHash = (uint)hash.Length,
                hCatAdmin = admin,
            };
            catalogInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustCatalogInfo>());
            Marshal.StructureToPtr(info, catalogInfo, false);
            return Run(WtdChoiceCatalog, catalogInfo);
        }
        finally
        {
            if (catalogInfo != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(catalogInfo);
            }

            Marshal.FreeHGlobal(hashBuffer);
            Marshal.FreeHGlobal(memberPath);
            Marshal.FreeHGlobal(memberTag);
            Marshal.FreeHGlobal(catalogPath);
        }
    }

    private static int Run(uint unionChoice, IntPtr union) =>
        Run(unionChoice, union, WtdRevokeNone, WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval);

    private static int Run(uint unionChoice, IntPtr union, uint revocationChecks, uint providerFlags)
    {
        var data = new WintrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
            dwUIChoice = WtdUiNone,
            fdwRevocationChecks = revocationChecks,
            dwUnionChoice = unionChoice,
            pUnion = union,
            dwStateAction = WtdStateActionVerify,
            dwProvFlags = providerFlags,
        };

        IntPtr dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustData>());
        try
        {
            Marshal.StructureToPtr(data, dataPtr, false);
            var action = GenericVerifyV2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);

            // Release the state WinVerifyTrust kept for the verify call.
            var closing = Marshal.PtrToStructure<WintrustData>(dataPtr);
            closing.dwStateAction = WtdStateActionClose;
            Marshal.StructureToPtr(closing, dataPtr, false);
            WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
        }
    }

    private static string? SignerName(string signedFile)
    {
        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(signedFile));
            string name = certificate.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(name) ? SignatureRules.CommonName(certificate.Subject) : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? CompanyName(string path)
    {
        try
        {
            string? company = FileVersionInfo.GetVersionInfo(path).CompanyName;
            return string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustCatalogInfo
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint cbStruct;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr data);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr catAdmin, IntPtr subsystem,
        [MarshalAs(UnmanagedType.LPWStr)] string? hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr catAdmin, SafeFileHandle file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr catAdmin, byte[] hash, uint hashSize, uint flags, IntPtr previousCatalog);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalogInfo, ref CatalogInfo info, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catalogInfo, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);
}

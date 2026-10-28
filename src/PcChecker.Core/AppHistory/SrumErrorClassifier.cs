namespace PcChecker.Core.AppHistory;

/// <summary>
/// Sorts the ways reading Windows' records can fail into a few plain reasons. It looks only at numbers (the exit code
/// of <c>esentutl</c>, ESE error codes, Win32 and HRESULT codes) and exception types, never at message text, so it
/// works the same on every Windows language.
/// </summary>
public static class SrumErrorClassifier
{
    // ESE (JET_err) codes. esentutl exits with the error it hit, so the same numbers come back as exit codes.
    private const int JetErrFileNotFound = -1811;
    private const int JetErrInvalidPath = -1023;
    private const int JetErrFileAccessDenied = -1032; // "the file is locked or in use"
    private const int JetErrPermissionDenied = -1809;
    private const int JetErrAccessDenied = -1907;
    private const int JetErrDatabaseDirtyShutdown = -550;
    private const int JetErrDatabaseCorrupted = -1206;
    private const int JetErrReadVerifyFailure = -1018;
    private const int JetErrPageNotInitialized = -1019;
    private const int JetErrDiskIO = -1022;
    private const int JetErrLogFileCorrupt = -501;
    private const int JetErrMissingLogFile = -528;
    private const int JetErrBadLogVersion = -514;
    private const int JetErrDatabaseInconsistent = -1218;
    private const int JetErrPageSizeMismatch = -1213;
    private const int JetErrDatabaseFileReadOnly = -1008;
    private const int JetErrInvalidDatabase = -1028;
    private const int JetErrObjectNotFound = -1305;

    // Win32 codes, alone or inside an HRESULT (0x8007xxxx).
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorTimeout = 1460;
    private const int ErrorElevationRequired = 740;

    /// <summary>Classifies an exit code of esentutl or an ESE error code. Zero is success.</summary>
    public static SrumFailureKind Classify(int code)
    {
        if (code == 0)
        {
            return SrumFailureKind.None;
        }

        // Volume Shadow Copy errors: VSS_E_* are 0x80042301 to 0x8004234F.
        uint unsigned = unchecked((uint)code);
        if (unsigned is >= 0x80042300 and <= 0x800423FF)
        {
            return SrumFailureKind.SnapshotFailed;
        }

        int win32 = (unsigned & 0xFFFF0000) == 0x80070000 ? (int)(unsigned & 0xFFFF) : code;
        return win32 switch
        {
            JetErrFileNotFound or JetErrInvalidPath or ErrorFileNotFound or ErrorPathNotFound => SrumFailureKind.Missing,
            JetErrFileAccessDenied or ErrorSharingViolation or ErrorLockViolation => SrumFailureKind.Locked,
            JetErrPermissionDenied or JetErrAccessDenied or ErrorAccessDenied or JetErrDatabaseFileReadOnly => SrumFailureKind.AccessDenied,
            ErrorElevationRequired => SrumFailureKind.NotAdministrator,
            ErrorTimeout => SrumFailureKind.TimedOut,
            JetErrDatabaseDirtyShutdown or JetErrDatabaseCorrupted or JetErrReadVerifyFailure or JetErrPageNotInitialized
                or JetErrDiskIO or JetErrLogFileCorrupt or JetErrMissingLogFile or JetErrBadLogVersion
                or JetErrDatabaseInconsistent or JetErrPageSizeMismatch or JetErrInvalidDatabase => SrumFailureKind.Corrupt,
            JetErrObjectNotFound => SrumFailureKind.Unsupported,
            _ => SrumFailureKind.Unknown,
        };
    }

    /// <summary>
    /// Classifies an exception. <paramref name="engineError"/> is the ESE error code when the exception came from the
    /// database engine (the Windows code passes it on), which then decides.
    /// </summary>
    public static SrumFailureKind Classify(Exception exception, int? engineError = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (engineError is { } jet && jet != 0)
        {
            return Classify(jet);
        }

        return exception switch
        {
            OperationCanceledException => SrumFailureKind.Cancelled,
            TimeoutException => SrumFailureKind.TimedOut,
            PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException => SrumFailureKind.Unsupported,
            UnauthorizedAccessException => SrumFailureKind.AccessDenied,
            FileNotFoundException or DirectoryNotFoundException => SrumFailureKind.Missing,
            IOException io => Classify(io.HResult),
            _ => exception.InnerException is { } inner ? Classify(inner) : SrumFailureKind.Unknown,
        };
    }

    /// <summary>The failure with its plain-language reason.</summary>
    public static SrumFailure Describe(SrumFailureKind kind) => new(kind, Message(kind));

    /// <summary>One sentence saying why Windows' records are not shown.</summary>
    public static string Message(SrumFailureKind kind) => kind switch
    {
        SrumFailureKind.None => "Windows' records were read.",
        SrumFailureKind.Unsupported => "This version of Windows does not keep app usage records that PC Checker can read.",
        SrumFailureKind.NotAdministrator => "Reading Windows' app usage records needs administrator rights.",
        SrumFailureKind.Missing => "Windows is not keeping app usage records on this PC (the Diagnostic Policy Service may be off, or a policy turns them off).",
        SrumFailureKind.Locked => "Windows' app usage records were in use and could not be copied. Try again in a minute.",
        SrumFailureKind.AccessDenied => "Windows did not allow PC Checker to read its app usage records.",
        SrumFailureKind.Corrupt => "The copy of Windows' app usage records was damaged or incomplete.",
        SrumFailureKind.SnapshotFailed => "Windows could not make the snapshot of drive C: needed to copy its app usage records.",
        SrumFailureKind.TimedOut => "Reading Windows' app usage records took too long.",
        SrumFailureKind.Cancelled => "Reading Windows' app usage records was cancelled.",
        _ => "Windows' app usage records could not be read.",
    };

    /// <summary>True when trying again later may work (the file was busy, a snapshot failed or it took too long).</summary>
    public static bool IsWorthRetrying(SrumFailureKind kind) =>
        kind is SrumFailureKind.Locked or SrumFailureKind.SnapshotFailed or SrumFailureKind.TimedOut or SrumFailureKind.Cancelled;
}

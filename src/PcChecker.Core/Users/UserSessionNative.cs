using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcChecker.Core.Users;

/// <summary>
/// P/Invoke wrappers over the Remote Desktop Services (WTS) and user32 APIs the Users tab needs: enumerate sessions,
/// read user, domain, client and state, disconnect, sign off, send a message, and lock the PC. Per-session processor
/// and memory totals are measured from the running processes. Everything is wrapped so a failure degrades gracefully.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserSessionNative
{
    private static readonly IntPtr CurrentServer = IntPtr.Zero;

    internal readonly record struct RawSession(int SessionId, int State, string UserName, string? Domain, string? ClientName, DateTimeOffset? LogonTime);

    internal readonly record struct SessionUsage(double CpuPercent, long MemoryBytes, int ProcessCount, DateTimeOffset? LogonTime);

    internal static IReadOnlyList<RawSession> EnumerateSessions()
    {
        var list = new List<RawSession>();
        if (!WTSEnumerateSessions(CurrentServer, 0, 1, out var buffer, out int count) || buffer == IntPtr.Zero)
        {
            return list;
        }

        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + (i * size));
                string user = Query(info.SessionId, WTS_INFO_CLASS.WTSUserName) ?? string.Empty;
                string? domain = Query(info.SessionId, WTS_INFO_CLASS.WTSDomainName);
                string? client = Query(info.SessionId, WTS_INFO_CLASS.WTSClientName);
                list.Add(new RawSession(info.SessionId, info.State, user, Empty(domain), Empty(client), QueryLogonTime(info.SessionId)));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return list;
    }

    /// <summary>
    /// Measures, with a short two-sample pass, each session's summed processor use, memory and process count, and the
    /// earliest process start time (a fallback for the sign-in time, used only when Windows does not report it).
    /// </summary>
    internal static Dictionary<int, SessionUsage> MeasureSessionUsage(CancellationToken cancellationToken)
    {
        var first = new Dictionary<int, (TimeSpan Cpu, long Mem, int Count, DateTimeOffset? Oldest)>();
        var cpuByPid = new Dictionary<int, TimeSpan>();
        var sessionByPid = new Dictionary<int, int>();

        foreach (var p in Processes())
        {
            try
            {
                int session = p.SessionId;
                cpuByPid[p.Id] = p.TotalProcessorTime;
                sessionByPid[p.Id] = session;
                var entry = first.TryGetValue(session, out var e) ? e : (TimeSpan.Zero, 0L, 0, (DateTimeOffset?)null);
                DateTimeOffset? start = TryStart(p);
                entry.Item1 += p.TotalProcessorTime;
                entry.Item2 += p.WorkingSet64;
                entry.Item3 += 1;
                if (start is { } s && (entry.Item4 is null || s < entry.Item4))
                {
                    entry.Item4 = s;
                }

                first[session] = entry;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // The process ended or is not readable; skip it.
            }
            finally
            {
                p.Dispose();
            }
        }

        var sw = Stopwatch.StartNew();
        try
        {
            Task.Delay(500, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        double elapsed = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        var cpuBySession = new Dictionary<int, double>();
        foreach (var p in Processes())
        {
            try
            {
                if (cpuByPid.TryGetValue(p.Id, out var before) && sessionByPid.TryGetValue(p.Id, out int session))
                {
                    double delta = (p.TotalProcessorTime - before).TotalSeconds;
                    cpuBySession[session] = cpuBySession.GetValueOrDefault(session) + delta;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        int processors = Math.Max(1, Environment.ProcessorCount);
        var result = new Dictionary<int, SessionUsage>();
        foreach (var (session, agg) in first)
        {
            double cpu = cpuBySession.TryGetValue(session, out var seconds) ? seconds / (elapsed * processors) * 100.0 : 0;
            result[session] = new SessionUsage(Math.Clamp(cpu, 0, 100), agg.Mem, agg.Count,
                agg.Oldest is { } o ? o : null);
        }

        return result;
    }

    internal static bool Disconnect(int sessionId) => WTSDisconnectSession(CurrentServer, sessionId, false);

    internal static bool Logoff(int sessionId) => WTSLogoffSession(CurrentServer, sessionId, false);

    internal static bool SendMessage(int sessionId, string title, string message)
    {
        return WTSSendMessage(CurrentServer, sessionId, title, title.Length * 2, message, message.Length * 2,
            0 /* MB_OK */, 0, out _, false);
    }

    internal static bool LockWorkstation() => LockWorkStation();

    private static IEnumerable<Process> Processes()
    {
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            yield break;
        }

        foreach (var p in all)
        {
            yield return p;
        }
    }

    private static DateTimeOffset? TryStart(Process p)
    {
        try
        {
            return p.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Query(int sessionId, WTS_INFO_CLASS infoClass)
    {
        if (!WTSQuerySessionInformation(CurrentServer, sessionId, infoClass, out var buffer, out _) || buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>The sign-in time Windows records for the session (WTSSessionInfo), or null when it reports none.</summary>
    private static DateTimeOffset? QueryLogonTime(int sessionId)
    {
        if (!WTSQuerySessionInformation(CurrentServer, sessionId, WTS_INFO_CLASS.WTSSessionInfo, out var buffer, out int bytes) || buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (bytes < Marshal.SizeOf<WTSINFOW>())
            {
                return null;
            }

            var info = Marshal.PtrToStructure<WTSINFOW>(buffer);
            return SessionStateMapper.LogonTimeFromFileTime(info.LogonTime);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
        WTSDomainName = 7,
        WTSClientName = 10,
        WTSSessionInfo = 24,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTSINFOW
    {
        public int State;
        public int SessionId;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
        public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
        public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pWinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, WTS_INFO_CLASS infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSDisconnectSession(IntPtr server, int sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr server, int sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSSendMessage(IntPtr server, int sessionId, string title, int titleLength,
        string message, int messageLength, int style, int timeoutSeconds, out int response, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}

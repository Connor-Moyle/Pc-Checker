using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PcChecker.Core.Scheduling;

/// <summary>A request sent to the running PC Checker over the <see cref="ScheduledJobPipe.CurrentPipeName"/> pipe.</summary>
public sealed record PipeRequest(PipeCommand Command, ScheduledJobKind? Job);

public enum PipeCommand
{
    /// <summary>Run a job (sent by <c>--scheduled &lt;job&gt;</c> when the app is already open).</summary>
    Run,

    /// <summary>Bring the window to the front (sent by a second launch).</summary>
    Show,
}

/// <summary>
/// One-line text protocol on the named pipe <c>PcChecker.Jobs.&lt;session&gt;.&lt;user SID&gt;</c>: the client writes <c>run &lt;job&gt;</c> or
/// <c>show</c>, the server answers <c>ok</c>, <c>busy</c> or <c>refused</c>. The pipe only accepts the current user.
/// </summary>
public static class ScheduledJobPipe
{
    /// <summary>Base name; the real pipe adds the Windows session and user (see <see cref="CurrentPipeName"/>).</summary>
    public const string PipeName = "PcChecker.Jobs";

    private static readonly Lazy<string> Current = new(() => NameFor(CurrentSessionId(), CurrentUserSid()));

    /// <summary>
    /// The pipe for this Windows session and user. Pipe names are machine-wide, so without the session and user a
    /// scheduled run in one person's session could hand its job to another person's PC Checker.
    /// </summary>
    public static string CurrentPipeName => Current.Value;

    /// <summary><c>PcChecker.Jobs.&lt;session&gt;.&lt;user SID&gt;</c> (the SID part is left out when unknown).</summary>
    public static string NameFor(int sessionId, string? userSid)
    {
        var name = PipeName + "." + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sid = new string((userSid ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return sid.Length == 0 ? name : name + "." + sid;
    }

    private static int CurrentSessionId()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    private static string? CurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }
    public const string ReplyOk = "ok";
    public const string ReplyBusy = "busy";
    public const string ReplyRefused = "refused";

    public static string Format(PipeRequest request) => request.Command == PipeCommand.Show
        ? "show"
        : "run " + ScheduledJobIds.Id(request.Job ?? throw new ArgumentException("A run request needs a job.", nameof(request)));

    /// <summary>Parses a request line; returns null for anything unexpected.</summary>
    public static PipeRequest? Parse(string? line)
    {
        var parts = (line ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && parts[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            return new PipeRequest(PipeCommand.Show, null);
        }

        if (parts.Length == 2 && parts[0].Equals("run", StringComparison.OrdinalIgnoreCase) && ScheduledJobIds.TryParse(parts[1], out var job))
        {
            return new PipeRequest(PipeCommand.Run, job);
        }

        return null;
    }

    /// <summary>Sends a request to a running instance. Returns its reply, or null when nobody answered in time.</summary>
    public static async Task<string?> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await using var client = new NamedPipeClientStream(".", CurrentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 256, leaveOpen: true);
            await writer.WriteLineAsync(Format(request).AsMemory(), cts.Token).ConfigureAwait(false);
            var reply = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            return reply?.Trim();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Listens on <see cref="ScheduledJobPipe.CurrentPipeName"/> in the running app and hands each request to a callback, which
/// returns the reply. Failures are logged and the listener keeps going until disposed.
/// </summary>
public sealed class ScheduledJobPipeServer : IDisposable
{
    private readonly Func<PipeRequest, string> _handler;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public ScheduledJobPipeServer(Func<PipeRequest, string> handler, ILogger? logger = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger ?? NullLogger.Instance;
    }

    public void Start() => _loop ??= Task.Run(() => ListenAsync(_cts.Token));

    private async Task ListenAsync(CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(ScheduledJobPipe.CurrentPipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                failures = 0;
                using var reader = new StreamReader(server, Encoding.UTF8, false, 256, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var lineTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                lineTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var line = await reader.ReadLineAsync(lineTimeout.Token).ConfigureAwait(false);
                var request = ScheduledJobPipe.Parse(line);
                string reply;
                if (request is null)
                {
                    _logger.LogWarning("Ignored an unexpected request on the job pipe.");
                    reply = ScheduledJobPipe.ReplyRefused;
                }
                else
                {
                    reply = _handler(request);
                }

                await writer.WriteLineAsync(reply.AsMemory(), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Another instance owning the name, a client that hung up early, or pipes unavailable: back off and retry.
                failures++;
                _logger.LogDebug(ex, "Job pipe listener error #{Count}.", failures);
                if (failures >= 20)
                {
                    _logger.LogWarning(ex, "The job pipe stopped after repeated errors; scheduled jobs will run in their own process.");
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, failures * 2)), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }
}

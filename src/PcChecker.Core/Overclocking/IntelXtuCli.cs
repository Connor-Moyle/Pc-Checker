using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Overclocking;

/// <summary>
/// Thin wrapper over the Intel XTU command line. Tunable ids differ between XTU versions, so every write first lists
/// the tunables and matches them by name; when a name cannot be matched the call fails with guidance instead of guessing.
/// </summary>
public sealed class IntelXtuCli
{
    private readonly string _path;
    private readonly ILogger _logger;

    public IntelXtuCli(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public string Path => _path;

    public async Task<OperationResult<IReadOnlyList<XtuTunable>>> ListTunablesAsync(CancellationToken cancellationToken)
    {
        var run = await RunAsync("-i", cancellationToken).ConfigureAwait(false);
        if (!run.Success)
        {
            return OperationResult<IReadOnlyList<XtuTunable>>.Fail(run.Message, run.Error);
        }

        var tunables = XtuOutputParser.Parse(run.Value);
        return tunables.Count == 0
            ? OperationResult<IReadOnlyList<XtuTunable>>.Fail("Intel XTU returned no tunables. Open XTU once so its service starts, then try again.")
            : OperationResult<IReadOnlyList<XtuTunable>>.Ok(tunables);
    }

    /// <summary>Sets a tunable by name; returns the id that was changed so it can be reset later.</summary>
    public async Task<OperationResult<int>> SetByNameAsync(IReadOnlyList<XtuTunable> tunables, string[] names, string value, CancellationToken cancellationToken)
    {
        var tunable = XtuOutputParser.Find(tunables, names);
        if (tunable is null)
        {
            return OperationResult<int>.Fail($"This XTU version does not expose \"{names[0]}\" by a known name. Change it inside Intel XTU or the BIOS instead.");
        }

        var run = await RunAsync($"-t -id {tunable.Id} -v {value}", cancellationToken).ConfigureAwait(false);
        if (!run.Success)
        {
            return OperationResult<int>.Fail($"XTU rejected {tunable.Name} = {value}: {run.Message}", run.Error);
        }

        _logger.LogInformation("XTU tunable {Name} (id {Id}) set to {Value}.", tunable.Name, tunable.Id, value);
        return OperationResult<int>.Ok(tunable.Id, $"{tunable.Name} set to {value}.");
    }

    public async Task<OperationResult> ResetAsync(IEnumerable<int> tunableIds, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var id in tunableIds.Distinct())
        {
            var run = await RunAsync($"-t -id {id} -v default", cancellationToken).ConfigureAwait(false);
            if (!run.Success)
            {
                failures.Add($"id {id}: {run.Message}");
            }
        }

        return failures.Count == 0 ? OperationResult.Ok("XTU tunables returned to defaults.") : OperationResult.Fail("Some XTU tunables could not be reset: " + string.Join("; ", failures));
    }

    public static string FormatRatio(int ratio) => ratio.ToString(CultureInfo.InvariantCulture);

    /// <summary>XTU takes the voltage offset in volts.</summary>
    public static string FormatVoltageOffset(int millivolts) => (millivolts / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    public static string FormatWatts(int watts) => watts.ToString(CultureInfo.InvariantCulture);

    private async Task<OperationResult<string>> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return OperationResult<string>.Fail("Intel XTU is only available on Windows.");
        }

        var psi = new ProcessStartInfo(_path, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // Already gone.
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return OperationResult<string>.Fail("Intel XTU did not answer within 45 seconds.");
            }

            string output = stdout.ToString();
            if (process.ExitCode != 0)
            {
                var err = stderr.ToString().Trim();
                return OperationResult<string>.Fail($"XTU exited with code {process.ExitCode}{(err.Length > 0 ? ": " + err : string.Empty)}");
            }

            return OperationResult<string>.Ok(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationResult<string>.Fail("Could not run Intel XTU: " + ex.Message, ex);
        }
    }
}

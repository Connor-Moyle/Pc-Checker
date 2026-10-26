namespace PcChecker.Core.Software;

/// <summary>
/// Waits for an uninstaller to finish its clean-up after the launched process has exited. NSIS uninstallers copy
/// themselves to a temp folder and the original process returns 0 at once while the copy does the removal, so the
/// registry entry can stay for a few seconds after "success". Pure: the probe is injected so it can be tested.
/// </summary>
public static class UninstallCompletionWaiter
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to keep re-checking before deciding the entry really is still there.</summary>
    public static TimeSpan GraceFor(InstallerKind installer, bool quiet) => installer switch
    {
        InstallerKind.Nsis => TimeSpan.FromSeconds(20),
        InstallerKind.Squirrel => TimeSpan.FromSeconds(10),
        InstallerKind.Unknown => TimeSpan.FromSeconds(10),
        _ => quiet ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2),
    };

    /// <summary>
    /// Polls <paramref name="stillRegistered"/> until it returns false or <paramref name="grace"/> elapses.
    /// Returns true when the entry is gone. A probe that throws counts as "gone" so a registry hiccup never blocks.
    /// </summary>
    public static async Task<bool> WaitUntilGoneAsync(Func<bool> stillRegistered, TimeSpan grace, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stillRegistered);
        if (interval <= TimeSpan.Zero)
        {
            interval = DefaultInterval;
        }

        var deadline = DateTime.UtcNow + grace;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool present;
            try
            {
                present = stillRegistered();
            }
            catch
            {
                return true;
            }

            if (!present)
            {
                return true;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(remaining < interval ? remaining : interval, cancellationToken).ConfigureAwait(false);
        }
    }
}

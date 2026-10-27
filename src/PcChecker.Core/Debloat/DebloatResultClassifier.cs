namespace PcChecker.Core.Debloat;

/// <summary>What the page reports for one tweak after a batch.</summary>
public enum DebloatResultKind
{
    Applied,
    Reverted,
    AlreadyApplied,
    Skipped,
    Failed,
}

/// <summary>One tweak's result as the page shows it, after the tweak's state was read again from the PC.</summary>
public sealed record DebloatResultView(string TweakId, string Title, DebloatResultKind Kind, DebloatVisibility Visibility, string Message)
{
    /// <summary>Short outcome for the pill: Applied, Reverted, Already applied, Skipped, Failed.</summary>
    public string Outcome => Kind switch
    {
        DebloatResultKind.AlreadyApplied => "Already applied",
        _ => Kind.ToString(),
    };

    /// <summary>The step still needed to see the change, or null.</summary>
    public string? NextStep => Kind is DebloatResultKind.Applied or DebloatResultKind.Reverted ? DebloatRestartRules.NextStep(Visibility) : null;

    /// <summary>The full label, for example "Applied – restart Explorer to see it".</summary>
    public string Label => (Kind, Visibility) switch
    {
        (DebloatResultKind.Applied or DebloatResultKind.Reverted, DebloatVisibility.RestartExplorer) => $"{Outcome} – restart Explorer to see it",
        (DebloatResultKind.Applied or DebloatResultKind.Reverted, DebloatVisibility.SignOut) => $"{Outcome} – sign out to see it",
        (DebloatResultKind.Applied or DebloatResultKind.Reverted, DebloatVisibility.RestartWindows) => $"{Outcome} – restart Windows to finish",
        _ => Outcome,
    };
}

/// <summary>The batch summary shown in the InfoBar.</summary>
public sealed record DebloatResultSummary(string Title, string Message, int Done, int Failed, bool NeedsExplorerRestart, bool NeedsSignOut, bool NeedsWindowsRestart);

/// <summary>
/// Pure classification of batch results: combines what the engine reported with the state read again afterwards, so a
/// change Windows did not keep is reported as failed (with a plain reason) instead of "Applied", and every applied
/// change says what still has to happen before it shows.
/// </summary>
public static class DebloatResultClassifier
{
    public const string NotKeptMessage =
        "Windows did not keep this change: the setting still reads as before. A policy from your organisation (work or school PC), security software or another tool may be setting it back.";

    /// <summary>Classifies one result. <paramref name="after"/> is the state read after the batch (null when unknown).</summary>
    public static DebloatResultView Classify(TweakRunResult result, DebloatTweak? tweak, TweakState? after, bool revert)
    {
        ArgumentNullException.ThrowIfNull(result);
        var visibility = tweak is null ? DebloatVisibility.Now : DebloatRestartRules.For(tweak);
        switch (result.Outcome)
        {
            case TweakOutcome.Failed:
                return new(result.TweakId, result.Title, DebloatResultKind.Failed, DebloatVisibility.Now, Explain(result.Message));
            case TweakOutcome.Skipped:
                return new(result.TweakId, result.Title, DebloatResultKind.Skipped, DebloatVisibility.Now, result.Message);
            case TweakOutcome.AlreadyApplied:
                return new(result.TweakId, result.Title, DebloatResultKind.AlreadyApplied, DebloatVisibility.Now, result.Message);
            case TweakOutcome.Reverted:
                return new(result.TweakId, result.Title, DebloatResultKind.Reverted, visibility, result.Message);
        }

        // Applied: trust it only when the re-read agrees (or cannot tell).
        if (!revert && after is not null)
        {
            if (after.Status == TweakStatus.NotApplied)
            {
                return new(result.TweakId, result.Title, DebloatResultKind.Failed, DebloatVisibility.Now, NotKeptMessage);
            }

            if (after.Status == TweakStatus.Partial)
            {
                return new(result.TweakId, result.Title, DebloatResultKind.Failed, DebloatVisibility.Now,
                    $"Only part of this change stuck ({after.Detail ?? "partly applied"}). Something reset the rest; apply it again or check for a policy from your organisation.");
            }
        }

        return new(result.TweakId, result.Title, DebloatResultKind.Applied, visibility, result.Message);
    }

    /// <summary>Classifies a whole batch, failures first, then skipped, then the rest in batch order.</summary>
    public static IReadOnlyList<DebloatResultView> ClassifyAll(IReadOnlyList<TweakRunResult> results, IReadOnlyDictionary<string, TweakState>? after, bool revert)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results
            .Select(r => Classify(r, DebloatCatalog.Find(r.TweakId), after is not null && after.TryGetValue(r.TweakId, out var s) ? s : null, revert))
            .OrderBy(v => v.Kind switch { DebloatResultKind.Failed => 0, DebloatResultKind.Skipped => 1, _ => 2 })
            .ToList();
    }

    /// <summary>"5 applied (2 need File Explorer restarted), 3 already applied, 1 skipped, 1 failed."</summary>
    public static DebloatResultSummary Summarize(IReadOnlyList<DebloatResultView> views, bool revert, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(views);
        var done = views.Where(v => v.Kind is DebloatResultKind.Applied or DebloatResultKind.Reverted).ToList();
        int explorer = done.Count(v => v.Visibility == DebloatVisibility.RestartExplorer);
        int signOut = done.Count(v => v.Visibility == DebloatVisibility.SignOut);
        int reboot = done.Count(v => v.Visibility == DebloatVisibility.RestartWindows);
        int already = views.Count(v => v.Kind == DebloatResultKind.AlreadyApplied);
        int skipped = views.Count(v => v.Kind == DebloatResultKind.Skipped);
        int failed = views.Count(v => v.Kind == DebloatResultKind.Failed);

        var waiting = new List<string>();
        if (explorer > 0)
        {
            waiting.Add($"{explorer} show{(explorer == 1 ? "s" : string.Empty)} after File Explorer restarts");
        }

        if (signOut > 0)
        {
            waiting.Add($"{signOut} after you sign out");
        }

        if (reboot > 0)
        {
            waiting.Add($"{reboot} after Windows restarts");
        }

        var parts = new List<string> { $"{done.Count} {(revert ? "reverted" : "applied")}" + (waiting.Count > 0 ? $" ({string.Join(", ", waiting)})" : string.Empty) };
        if (already > 0)
        {
            parts.Add($"{already} already applied");
        }

        if (skipped > 0)
        {
            parts.Add($"{skipped} skipped");
        }

        if (failed > 0)
        {
            parts.Add($"{failed} failed");
        }

        var message = string.Join(", ", parts) + ".";
        if (failed > 0)
        {
            message += " The results below say why.";
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            message += " " + note.Trim();
        }

        string title = (revert, failed > 0, done.Count + already > 0) switch
        {
            (_, true, false) => revert ? "Revert failed" : "Debloat failed",
            (_, true, true) => revert ? "Revert finished with problems" : "Debloat finished with problems",
            _ => revert ? "Revert finished" : "Debloat finished",
        };
        return new DebloatResultSummary(title, message, done.Count + already, failed, explorer > 0, signOut > 0, reboot > 0);
    }

    /// <summary>Adds a plain-English hint to the common failure messages.</summary>
    public static string Explain(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The change failed and Windows gave no reason.";
        }

        if (message.Contains("access denied", StringComparison.OrdinalIgnoreCase) || message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return message.TrimEnd() + " Windows protects this setting, or your organisation manages it.";
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return message.TrimEnd() + " Try again; if it keeps happening, restart Windows first.";
        }

        return message;
    }
}

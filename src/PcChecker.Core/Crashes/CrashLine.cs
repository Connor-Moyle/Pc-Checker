namespace PcChecker.Core.Crashes;

/// <summary>
/// One plain sentence the Crashes page shows, kept as an English format and its values so it can be shown in the
/// page's language (<see cref="Text"/>) and copied in English for a support request (<see cref="ToString"/>).
/// </summary>
public sealed record CrashLine(string Format, params object?[] Args)
{
    /// <summary>The sentence in the language of <paramref name="tr"/> (English when null).</summary>
    public string Text(Localization.Translator? tr = null)
    {
        tr ??= Localization.Translator.English;
        var args = Args.Select(a => a switch
        {
            LocalizedText t => tr.T(t.English),
            LocalizedList l => string.Join(", ", l.English.Select(tr.T)),
            CrashLine inner => inner.Text(tr),
            _ => a,
        }).ToArray();
        return tr.F(Format, args);
    }

    public override string ToString() => Text();

    /// <summary>A line with no values.</summary>
    public static CrashLine Of(string text) => new(text);
}

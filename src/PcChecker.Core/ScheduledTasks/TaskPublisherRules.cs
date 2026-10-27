using PcChecker.Core.Processes;

namespace PcChecker.Core.ScheduledTasks;

/// <summary>Who a task comes from: Microsoft or another company, and the name to show. Pure.</summary>
public static class TaskPublisherRules
{
    // "Microsoft" in the languages Windows writes it in its own task authors.
    private static readonly string[] MicrosoftNames = ["Microsoft", "Майкрософт", "マイクロソフト", "微软", "微軟", "마이크로소프트"];

    /// <summary>
    /// True for Microsoft's tasks: in the \Microsoft\ folder or with a Microsoft author, unless the program it runs
    /// carries no valid signature or another company's signature (then it only claims to be from Microsoft).
    /// </summary>
    public static bool IsMicrosoft(string taskPath, string? author, SignatureInfo signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        bool claims = TaskPaths.IsMicrosoftFolder(taskPath) || IsMicrosoftAuthor(author);
        // A task that does not say it is from Microsoft is not, even when it runs a Microsoft program such as
        // powershell.exe; one that says so but runs an unsigned or other company's program only claims it.
        if (!claims || signature.IsMissingOrBroken)
        {
            return false;
        }

        return !(signature.IsSigned && !string.IsNullOrWhiteSpace(signature.Publisher) && !IsMicrosoftName(signature.Publisher));
    }

    /// <summary>
    /// Microsoft in any of Windows' languages, or a Windows resource reference such as
    /// "$(@%SystemRoot%\system32\wininet.dll,-16000)" (only Windows' own tasks use those).
    /// </summary>
    public static bool IsMicrosoftAuthor(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            return false;
        }

        var a = author.Trim();
        return a.StartsWith("$(@%SystemRoot%", StringComparison.OrdinalIgnoreCase)
               || a.StartsWith("$(@%windir%", StringComparison.OrdinalIgnoreCase)
               || IsMicrosoftName(a);
    }

    private static bool IsMicrosoftName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && MicrosoftNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name to show in the "From" column: the signer, else the author, else "Microsoft" or "Unknown".</summary>
    public static string Display(string taskPath, string? author, SignatureInfo signature, bool isMicrosoft)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.IsSigned && !string.IsNullOrWhiteSpace(signature.Publisher))
        {
            return signature.Publisher.Trim();
        }

        if (!string.IsNullOrWhiteSpace(author) && !author.TrimStart().StartsWith("$(", StringComparison.Ordinal) && !author.TrimStart().StartsWith("S-1-", StringComparison.Ordinal))
        {
            // Authors are often "DOMAIN\user" of whoever created the task.
            return author.Trim();
        }

        return isMicrosoft ? "Microsoft" : "Unknown";
    }
}

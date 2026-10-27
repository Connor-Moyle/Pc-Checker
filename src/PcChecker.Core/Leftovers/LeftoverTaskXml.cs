using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PcChecker.Core.Leftovers;

/// <summary>The actions of one scheduled task, as read from its XML definition.</summary>
/// <param name="Commands">The program of every Exec action, as written (may hold %variables% or quotes).</param>
/// <param name="HasOtherActions">True when the task also has COM handler, e-mail or message actions.</param>
/// <param name="Author">The Author element, when present.</param>
/// <param name="UserId">The account the task runs as (Principal/UserId: a SID, DOMAIN\user or user), when present.</param>
/// <param name="LogonType">Principal/LogonType ("InteractiveToken", "Password", "S4U"…), when present.</param>
public sealed record LeftoverTaskActions(IReadOnlyList<string> Commands, bool HasOtherActions, string? Author, string? UserId = null, string? LogonType = null)
{
    /// <summary>
    /// True when the task runs "whether the user is signed in or not" with a stored password: Task Scheduler needs that
    /// password again to register it, so it cannot be put back from its XML alone.
    /// </summary>
    public bool NeedsPassword => string.Equals(LogonType, "Password", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the actions of a Task Scheduler definition (the XML files Windows keeps under System32\Tasks, which is also
/// what <c>schtasks /Query /XML</c> prints). Element names are matched without their namespace so both forms work,
/// and DTDs are refused. Pure.
/// </summary>
public static class LeftoverTaskXml
{
    /// <summary>The actions of a task, or null when the text is not a task definition.</summary>
    public static LeftoverTaskActions? ReadActions(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('\uFEFF')), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        if (doc.Root is null || doc.Root.Name.LocalName != "Task")
        {
            return null;
        }

        var actions = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Actions");
        var commands = new List<string>();
        bool other = false;
        foreach (var action in actions?.Elements() ?? [])
        {
            if (action.Name.LocalName == "Exec")
            {
                var command = action.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim();
                if (!string.IsNullOrWhiteSpace(command))
                {
                    commands.Add(command);
                }
                else
                {
                    other = true;
                }
            }
            else
            {
                other = true;
            }
        }

        var author = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "RegistrationInfo")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Author")?.Value.Trim();

        // The principal the actions run under: the one the Actions element names in its Context, else the first.
        var principals = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Principals")?
            .Elements().Where(e => e.Name.LocalName == "Principal").ToList() ?? [];
        var context = actions?.Attribute("Context")?.Value;
        var principal = principals.FirstOrDefault(p => context is not null && string.Equals(p.Attribute("id")?.Value, context, StringComparison.Ordinal))
                        ?? principals.FirstOrDefault();
        string? Child(string name) => principal?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        return new LeftoverTaskActions(commands, other, string.IsNullOrWhiteSpace(author) ? null : author, Child("UserId"), Child("LogonType"));
    }

    /// <summary>The text of a task file, whatever byte-order mark it has (Windows writes UTF-16).</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The task path for a definition file under the Tasks folder: <c>C:\Windows\System32\Tasks\Vendor\Update</c> →
    /// <c>\Vendor\Update</c>; null when the file is not inside the folder.
    /// </summary>
    public static string? TaskPathFromFile(string tasksFolder, string file)
    {
        var relative = DiskSpace.DiskSpacePaths.RelativeSegments(file, tasksFolder);
        return relative is null || relative.Count == 0 ? null : "\\" + string.Join("\\", relative);
    }
}

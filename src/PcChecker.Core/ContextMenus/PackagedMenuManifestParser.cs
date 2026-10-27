using System.Xml;
using System.Xml.Linq;

namespace PcChecker.Core.ContextMenus;

/// <summary>One right-click verb a packaged app declares in its manifest.</summary>
public sealed record PackagedMenuVerb(string ItemType, string VerbId, string Clsid, string? ComPath);

/// <summary>What the right-click menu needs from an AppxManifest.xml.</summary>
public sealed record PackagedMenuManifest(
    string PackageName,
    string? Publisher,
    string? DisplayName,
    string? PublisherDisplayName,
    IReadOnlyList<PackagedMenuVerb> Verbs);

/// <summary>
/// Reads the Windows 11 right-click entries (<c>desktop4:FileExplorerContextMenus</c> → <c>desktop5:ItemType</c> →
/// <c>desktop5:Verb</c>) from a package manifest. Namespaces vary between manifest versions, so elements are matched
/// by local name. Never throws: a manifest that cannot be read gives null.
/// </summary>
public static class PackagedMenuManifestParser
{
    public static PackagedMenuManifest? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = doc.Root;
        if (root is null)
        {
            return null;
        }

        var identity = Descendants(root, "Identity").FirstOrDefault();
        string? name = Attr(identity, "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var properties = Descendants(root, "Properties").FirstOrDefault();
        string? displayName = properties is null ? null : Child(properties, "DisplayName");
        string? publisherDisplayName = properties is null ? null : Child(properties, "PublisherDisplayName");

        // COM classes declared by the package: Id → DLL or EXE path relative to the package folder.
        var comPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in Descendants(root, "Class"))
        {
            if (ShellExtensionBlockList.Normalize(Attr(cls, "Id")) is { } id && Attr(cls, "Path") is { } path)
            {
                comPaths.TryAdd(id, path);
            }
            else if (ShellExtensionBlockList.Normalize(Attr(cls, "Id")) is { } exeClassId
                     && cls.Parent?.Parent is { } server && Attr(server, "Executable") is { } exe)
            {
                comPaths.TryAdd(exeClassId, exe);
            }
        }

        var verbs = new List<PackagedMenuVerb>();
        foreach (var menus in Descendants(root, "FileExplorerContextMenus"))
        {
            foreach (var itemType in menus.Elements().Where(e => e.Name.LocalName == "ItemType"))
            {
                string type = Attr(itemType, "Type") ?? "*";
                foreach (var verb in itemType.Elements().Where(e => e.Name.LocalName == "Verb"))
                {
                    if (ShellExtensionBlockList.Normalize(Attr(verb, "Clsid")) is not { } clsid)
                    {
                        continue;
                    }

                    string id = Attr(verb, "Id") ?? clsid;
                    comPaths.TryGetValue(clsid, out var comPath);
                    verbs.Add(new PackagedMenuVerb(type, id, clsid, comPath));
                }
            }
        }

        return new PackagedMenuManifest(name, Attr(identity, "Publisher"), displayName, publisherDisplayName, verbs);
    }

    private static IEnumerable<XElement> Descendants(XElement root, string localName) =>
        root.DescendantsAndSelf().Where(e => e.Name.LocalName == localName);

    private static string? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim() is { Length: > 0 } v ? v : null;

    private static string? Attr(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
}

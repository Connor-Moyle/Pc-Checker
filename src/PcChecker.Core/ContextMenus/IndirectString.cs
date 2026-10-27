using System.Globalization;
using System.Text;

namespace PcChecker.Core.ContextMenus;

/// <summary>
/// Indirect strings ("@%SystemRoot%\system32\shell32.dll,-8506", "@{Package?ms-resource://…}") and menu text clean-up.
/// Resolving needs SHLoadIndirectString on Windows; these helpers decide what to ask for and what to show when it fails.
/// </summary>
public static class IndirectString
{
    /// <summary>True for "@file,-id", "@file,id" and "@{package?resource}" strings.</summary>
    public static bool IsIndirect(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('@') && text.Trim().Length > 1;

    /// <summary>
    /// Splits "@path,-123" (optionally followed by ";v2"-style suffixes) into the file and the resource id.
    /// Returns false for package resources and for text that is not an indirect string.
    /// </summary>
    public static bool TryParseResource(string? text, out string file, out int resourceId)
    {
        file = string.Empty;
        resourceId = 0;
        if (!IsIndirect(text))
        {
            return false;
        }

        string body = text!.Trim()[1..];
        if (body.StartsWith('{'))
        {
            return false;
        }

        int semicolon = body.IndexOf(';');
        if (semicolon >= 0)
        {
            body = body[..semicolon];
        }

        int comma = body.LastIndexOf(',');
        if (comma <= 0 || comma == body.Length - 1)
        {
            return false;
        }

        string idText = body[(comma + 1)..].Trim();
        if (!int.TryParse(idText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out resourceId))
        {
            return false;
        }

        file = body[..comma].Trim().Trim('"');
        return file.Length > 0;
    }

    /// <summary>True for "@{PackageFullName?ms-resource://…}" (text from a Store app's resources).</summary>
    public static bool IsPackageResource(string? text) =>
        IsIndirect(text) && text!.Trim()[1..].StartsWith('{') && text.Contains('?') && text.TrimEnd().EndsWith('}');

    /// <summary>
    /// Builds the indirect string for a packaged app's "ms-resource:" text so SHLoadIndirectString can read it.
    /// "ms-resource:AppName" becomes "@{Full_Name?ms-resource://Family/Resources/AppName}".
    /// </summary>
    public static string? ForPackage(string? packageFullName, string? packageName, string? resource)
    {
        if (string.IsNullOrWhiteSpace(packageFullName) || string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        const string Prefix = "ms-resource:";
        if (!resource.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = resource[Prefix.Length..];
        string uri;
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            uri = Prefix + rest;
        }
        else if (rest.StartsWith('/'))
        {
            uri = $"{Prefix}//{packageName}{rest}";
        }
        else
        {
            uri = rest.Contains('/') ? $"{Prefix}//{packageName}/{rest}" : $"{Prefix}//{packageName}/Resources/{rest}";
        }

        return "@{" + packageFullName + "?" + uri + "}";
    }

    /// <summary>
    /// The text File Explorer shows: the resolved string when there is one, the raw text when it is plain, and a
    /// readable form of the key name when an indirect string could not be resolved.
    /// </summary>
    public static string DisplayText(string? raw, string? resolved, string keyName)
    {
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return CleanMenuText(resolved);
        }

        if (!string.IsNullOrWhiteSpace(raw) && !IsIndirect(raw))
        {
            return CleanMenuText(raw);
        }

        return Humanize(keyName);
    }

    /// <summary>Removes access-key ampersands ("&amp;Open" → "Open", "A &amp;&amp; B" → "A &amp; B") and trims.</summary>
    public static string CleanMenuText(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '&')
            {
                if (i + 1 < text.Length && text[i + 1] == '&')
                {
                    sb.Append('&');
                    i++;
                }

                continue;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>"OpenWithCode" → "Open With Code"; "{GUID}" and empty names stay as they are.</summary>
    public static string Humanize(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return "(no name)";
        }

        string name = keyName.Trim();
        if (name.StartsWith('{'))
        {
            return name;
        }

        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c is '_' or '.')
            {
                sb.Append(' ');
                continue;
            }

            if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]))
            {
                sb.Append(' ');
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}

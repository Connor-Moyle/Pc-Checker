namespace PcChecker.Core.Debloat;

/// <summary>Pure rules: which tweaks fit this Windows build, and whether a printer is installed.</summary>
public static class DebloatRules
{
    /// <summary>Printers Windows installs on every PC; they do not count as "a printer you use".</summary>
    private static readonly string[] VirtualPrinterPrefixes =
    [
        "Microsoft Print to PDF", "Microsoft XPS Document Writer", "Fax", "OneNote", "Send To OneNote", "Microsoft Shared Fax",
        "Adobe PDF", "PDF24", "PDFCreator", "Root Print Queue",
    ];

    /// <summary>True when the tweak can be applied on <paramref name="build"/>; an unknown build is allowed.</summary>
    public static bool IsSupported(DebloatTweak tweak, int? build)
    {
        ArgumentNullException.ThrowIfNull(tweak);
        if (build is null)
        {
            return true;
        }

        if (tweak.MinWindowsBuild is int min && build < min)
        {
            return false;
        }

        return tweak.MaxWindowsBuild is not int max || build <= max;
    }

    /// <summary>Plain-English reason a tweak does not apply to this Windows (empty when it does).</summary>
    public static string UnsupportedReason(DebloatTweak tweak, int? build)
    {
        if (IsSupported(tweak, build))
        {
            return string.Empty;
        }

        if (tweak.MaxWindowsBuild is int max && build > max)
        {
            return "Only for Windows 10";
        }

        return tweak.MinWindowsBuild switch
        {
            DebloatCatalog.Windows11 => "Needs Windows 11",
            DebloatCatalog.Windows11_22H2 => "Needs Windows 11 22H2 or newer",
            DebloatCatalog.Windows11_23H2 => "Needs Windows 11 23H2 or newer",
            DebloatCatalog.Windows11_24H2 => "Needs Windows 11 24H2 or newer",
            int min => $"Needs Windows build {min} or newer",
            _ => "Not available on this Windows",
        };
    }

    /// <summary>Printers that look physical (excludes PDF/XPS/OneNote/fax drivers Windows and apps add).</summary>
    public static IReadOnlyList<string> PhysicalPrinters(IEnumerable<string> printerNames) =>
        printerNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Where(n => !VirtualPrinterPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Turns a printer-connection key name (",,server,printer", how Windows stores \\server\printer under
    /// Printers\Connections) back into the UNC name the user knows. Other names are returned unchanged.
    /// </summary>
    public static string ConnectionKeyToPrinterName(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName) || !keyName.StartsWith(",,", StringComparison.Ordinal))
        {
            return keyName;
        }

        return @"\\" + keyName[2..].Replace(',', '\\');
    }

    /// <summary>Returns a reason the tweak must not be applied now, or null when it may.</summary>
    public static string? PreconditionFailure(DebloatTweak tweak, IReadOnlyList<string> printerNames)
    {
        if (tweak.Precondition != DebloatPrecondition.NoPhysicalPrinters)
        {
            return null;
        }

        var physical = PhysicalPrinters(printerNames);
        return physical.Count == 0
            ? null
            : $"{physical.Count} printer{(physical.Count == 1 ? string.Empty : "s")} installed ({string.Join(", ", physical.Take(3))}); left on so printing keeps working.";
    }

    /// <summary>The tweaks named by <paramref name="ids"/> plus every tweak with an operation on the same setting.</summary>
    public static IReadOnlyList<DebloatTweak> WithRelated(IEnumerable<string> ids, IEnumerable<DebloatTweak> all)
    {
        var allList = all.ToList();
        var idSet = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(allList.Where(t => idSet.Contains(t.Id)).SelectMany(t => t.Operations).Select(o => o.Key), StringComparer.OrdinalIgnoreCase);
        return allList.Where(t => idSet.Contains(t.Id) || t.Operations.Any(o => keys.Contains(o.Key))).ToList();
    }

    /// <summary>
    /// Pairs from <paramref name="selected"/> that conflict (see <see cref="Conflicts"/>), as readable titles.
    /// </summary>
    public static IReadOnlyList<(DebloatTweak A, DebloatTweak B)> ConflictingPairs(IReadOnlyCollection<DebloatTweak> selected)
    {
        var byId = selected.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        return Conflicts(selected).Select(p => (byId[p.A], byId[p.B])).ToList();
    }

    /// <summary>
    /// Tweaks that write the same registry value with different data (e.g. search icon vs. hidden search). A preset or
    /// selection must not contain both of a pair.
    /// </summary>
    public static IReadOnlyList<(string A, string B)> Conflicts(IEnumerable<DebloatTweak> tweaks)
    {
        var list = tweaks.ToList();
        var result = new List<(string, string)>();
        for (int i = 0; i < list.Count; i++)
        {
            for (int j = i + 1; j < list.Count; j++)
            {
                var a = list[i].Operations.OfType<RegistryValueOp>();
                var b = list[j].Operations.OfType<RegistryValueOp>().ToList();
                if (a.Any(x => b.Any(y => y.Key.Equals(x.Key, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.AppliedValue, y.AppliedValue, StringComparison.OrdinalIgnoreCase))))
                {
                    result.Add((list[i].Id, list[j].Id));
                }
            }
        }

        return result;
    }
}

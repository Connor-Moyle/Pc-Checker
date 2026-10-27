using System.Text;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>
/// Builds the maker's support page for this exact board or PC: ASUS, MSI, GIGABYTE/AORUS, ASRock boards by product
/// name; Dell by service tag; Lenovo by machine type; HP by model; Surface and Framework their driver pages. When a
/// maker's page needs something PC Checker cannot know (BIOSTAR's product id, an unknown GIGABYTE revision) it falls
/// back to the maker's search or download centre and says so (<see cref="PlatformLink.Exact"/> is false). Every URL is
/// HTTPS and every model is escaped. Pure.
/// </summary>
public static class SupportPageBuilder
{
    /// <summary>The support page for the PC's drivers and BIOS, or null when the maker is unknown.</summary>
    public static PlatformLink? For(PcPlatformInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.IsSelfBuilt && info.SystemMaker is not (PcMaker.Unknown or PcMaker.Other))
        {
            return ForSystem(info);
        }

        return info.BoardMaker is PcMaker.Unknown or PcMaker.Other ? null : ForBoard(info.BoardMaker, info.BoardProduct, info.BoardRevision, info.Chipset);
    }

    /// <summary>The support page of a motherboard.</summary>
    public static PlatformLink ForBoard(PcMaker maker, string? product, string? revision, ChipsetPlatform chipset)
    {
        var name = SmbiosNormalizer.MakerName(maker);
        var model = SmbiosNormalizer.BoardNameWithoutCode(product);
        if (model is null)
        {
            return General(maker, name, "Pick your board on the page.");
        }

        var title = $"{name} {model} support page";
        var note = "Drivers and BIOS for your board.";
        switch (maker)
        {
            case PcMaker.Asus:
                return new PlatformLink(title, $"https://www.asus.com/supportonly/{Escape(model)}/helpdesk_bios/", note, true);
            case PcMaker.Msi:
                return new PlatformLink(title, $"https://www.msi.com/Motherboard/{Slug(model, keepCase: true)}/support", note, true);
            case PcMaker.Gigabyte:
                var rev = RevisionSlug(revision);
                return rev is null
                    ? new PlatformLink($"{name} search for {model}", $"https://www.gigabyte.com/Search?kw={Escape(model)}", "Pick your board's revision (printed on the board) in the results.", false)
                    : new PlatformLink(title, $"https://www.gigabyte.com/Motherboard/{Slug(model, keepCase: true)}-rev-{rev}/support#support-dl-bios", note, true);
            case PcMaker.AsRock:
                return chipset is ChipsetPlatform.Amd or ChipsetPlatform.Intel
                    ? new PlatformLink(title, $"https://www.asrock.com/mb/{(chipset == ChipsetPlatform.Amd ? "AMD" : "Intel")}/{Escape(model)}/index.asp#BIOS", note, true)
                    : new PlatformLink($"{name} downloads", "https://www.asrock.com/support/index.asp?cat=BIOS", $"Search for {model} on the page.", false);
            case PcMaker.Biostar:
                return new PlatformLink($"{name} downloads", "https://www.biostar.com.tw/app/en/support/download.php", $"Search for {model} on the page.", false);
            case PcMaker.Nzxt:
                return new PlatformLink($"{name} support", "https://nzxt.com/support", $"Look for {model} under motherboards.", false);
            default:
                return General(maker, name, $"Search for {model} on the page.");
        }
    }

    /// <summary>The support page of a brand PC or laptop.</summary>
    public static PlatformLink ForSystem(PcPlatformInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var name = info.SystemMakerName ?? SmbiosNormalizer.MakerName(info.SystemMaker);
        var model = info.FriendlyModel ?? info.SystemModel;
        var shown = model is null ? name : model.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? model : $"{name} {model}";
        var title = $"{shown} support page";
        switch (info.SystemMaker)
        {
            case PcMaker.Dell:
                if (info.ServiceTag is { } tag)
                {
                    return new PlatformLink(title, $"https://www.dell.com/support/home/en-us/product-support/servicetag/{Escape(tag)}/drivers",
                        $"Opens Dell's page for service tag {tag}: drivers and BIOS made for this exact PC.", true);
                }

                return model is null
                    ? General(PcMaker.Dell, name, "Enter your service tag on the page.")
                    : new PlatformLink(title, $"https://www.dell.com/support/home/en-us/product-support/product/{Slug(model, keepCase: false)}/drivers", "Drivers and BIOS for your model.", true);
            case PcMaker.Lenovo:
                return info.MachineType is { } mt
                    ? new PlatformLink(title, $"https://pcsupport.lenovo.com/us/en/products/{Escape(mt)}", $"Lenovo's page for machine type {mt}.", true)
                    : model is null
                        ? General(PcMaker.Lenovo, name, "Enter your serial number on the page.")
                        : new PlatformLink(title, $"https://pcsupport.lenovo.com/us/en/search?query={Escape(model)}", "Pick your model in the results.", false);
            case PcMaker.Hp:
                return model is null
                    ? General(PcMaker.Hp, name, "Enter your serial number on the page.")
                    : new PlatformLink(title, $"https://support.hp.com/us-en/search?q={Escape(model)}", "Pick your model; HP may ask for the serial number.", false);
            case PcMaker.Acer:
                return new PlatformLink($"{name} drivers and manuals", "https://www.acer.com/us-en/support/drivers-and-manuals",
                    model is null ? "Enter your serial number on the page." : $"Search for {model} or enter your serial number.", false);
            case PcMaker.Microsoft:
                return new PlatformLink("Surface drivers and firmware", "https://support.microsoft.com/en-us/surface/drivers-firmware/download-drivers-and-firmware-for-surface",
                    model is null ? "Pick your Surface on the page." : $"Pick {model} on the page. Windows Update also installs Surface firmware.", false);
            case PcMaker.Framework:
                return new PlatformLink("Framework BIOS and drivers", "https://knowledgebase.frame.work/bios-and-drivers-downloads-rJ3PaCexh",
                    model is null ? "Pick your Framework model on the page." : $"Pick {model} on the page.", false);
            case PcMaker.Asus:
                return model is null
                    ? General(PcMaker.Asus, name, "Pick your model on the page.")
                    : new PlatformLink(title, $"https://www.asus.com/supportonly/{Escape(model)}/helpdesk_download/", "Drivers and BIOS for your model.", true);
            case PcMaker.Msi:
                return model is null
                    ? General(PcMaker.Msi, name, "Pick your model on the page.")
                    : new PlatformLink($"{name} search for {model}", $"https://www.msi.com/search/{Escape(model)}", "Pick your model in the results.", false);
            case PcMaker.Gigabyte:
                return model is null
                    ? General(PcMaker.Gigabyte, name, "Pick your model on the page.")
                    : new PlatformLink($"{name} search for {model}", $"https://www.gigabyte.com/Search?kw={Escape(model)}", "Pick your model in the results.", false);
            default:
                return ForBoard(info.BoardMaker, info.BoardProduct, info.BoardRevision, info.Chipset);
        }
    }

    /// <summary>The maker's general driver page when the exact model is unknown.</summary>
    public static PlatformLink General(PcMaker maker, string name, string note)
    {
        var url = maker switch
        {
            PcMaker.Asus => "https://www.asus.com/support/download-center/",
            PcMaker.Msi => "https://www.msi.com/support/download",
            PcMaker.Gigabyte => "https://www.gigabyte.com/Support",
            PcMaker.AsRock => "https://www.asrock.com/support/index.asp?cat=BIOS",
            PcMaker.Biostar => "https://www.biostar.com.tw/app/en/support/download.php",
            PcMaker.Nzxt => "https://nzxt.com/support",
            PcMaker.Dell => "https://www.dell.com/support/home/en-us?app=drivers",
            PcMaker.Hp => "https://support.hp.com/us-en/drivers",
            PcMaker.Lenovo => "https://pcsupport.lenovo.com/",
            PcMaker.Acer => "https://www.acer.com/us-en/support/drivers-and-manuals",
            PcMaker.Microsoft => "https://support.microsoft.com/en-us/surface/drivers-firmware/download-drivers-and-firmware-for-surface",
            PcMaker.Framework => "https://knowledgebase.frame.work/bios-and-drivers-downloads-rJ3PaCexh",
            _ => "https://support.microsoft.com/windows/update-drivers-manually-in-windows-ec62f46c-ff14-c91d-eead-d7126dc1f7b6",
        };
        return new PlatformLink($"{name} drivers", url, note, false);
    }

    /// <summary>
    /// The path part MSI, GIGABYTE and Dell use: spaces and other separators become single dashes, slashes too.
    /// "PRO B650-P WIFI" → "PRO-B650-P-WIFI"; "B550M PRO-VDH/WIFI" → "B550M-PRO-VDH-WIFI"; "XPS 15 9530" → "xps-15-9530".
    /// </summary>
    public static string Slug(string model, bool keepCase)
    {
        ArgumentNullException.ThrowIfNull(model);
        var sb = new StringBuilder();
        foreach (char c in model.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(keepCase ? c : char.ToLowerInvariant(c));
            }
            else if (c is '(' or ')')
            {
                // Brackets are dropped, not turned into dashes: "B550 (WIFI)" → "B550-WIFI".
                continue;
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        return sb.ToString().Trim('-');
    }

    /// <summary>GIGABYTE's revision in a URL: "1.0" → "10", "1.1" → "11"; null when unknown.</summary>
    public static string? RevisionSlug(string? revision)
    {
        var rev = SmbiosNormalizer.BoardRevision(revision);
        return rev is null ? null : rev.Replace(".", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Escapes a model for a URL path or query: spaces become %20, slashes %2F, so the model stays one segment.</summary>
    public static string Escape(string value) => Uri.EscapeDataString(value.Trim());
}

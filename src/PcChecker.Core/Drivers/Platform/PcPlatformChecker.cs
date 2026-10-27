using System.Globalization;
using System.Net;
using PcChecker.Core.Common;
using PcChecker.Core.Drivers.Sources;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>Fetches one maker URL over HTTPS: (url, referrer or null, allowed hosts, token) → text.</summary>
public delegate Task<string> PlatformFetch(string url, string? referrer, IReadOnlyList<string> allowedHosts, CancellationToken cancellationToken);

/// <summary>
/// Builds the <see cref="PcPlatformReport"/> from SMBIOS facts: the support page, the BIOS with its age (and the
/// maker's newest BIOS when a public catalog has it), and the chipset driver compared with AMD's page or linked to
/// Intel's. Maker answers go through <see cref="PlatformLookupCache"/> (12 hours). Offline, or with a source switched
/// off, the report still shows everything that is read locally and says what could not be checked. Shared by the
/// Windows and demo services; the network is only reached through the fetch delegate.
/// </summary>
public sealed class PcPlatformChecker
{
    public const string IntelChipsetPage = "https://www.intel.com/content/www/us/en/download/19347/chipset-inf-utility.html";

    private readonly PlatformLookupCache _cache;
    private readonly PlatformFetch _fetch;
    private readonly Func<DateTime> _today;

    public PcPlatformChecker(PlatformLookupCache cache, PlatformFetch fetch, Func<DateTime>? today = null)
    {
        _cache = cache;
        _fetch = fetch;
        _today = today ?? (() => DateTime.Today);
    }

    /// <summary>Builds the report. <paramref name="online"/> false uses saved answers only.</summary>
    public async Task<PcPlatformReport> BuildAsync(SmbiosFacts facts, string? installedAmdChipset, DriverSourceSettings settings, bool online, bool forceRefresh,
        IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(settings);
        var info = SmbiosNormalizer.Normalize(facts);
        var support = SupportPageBuilder.For(info);
        var notes = new List<string>();

        var chipset = await ChipsetAsync(info, installedAmdChipset, support, settings, online, forceRefresh, progress, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<MakerCatalogEntry> entries = [];
        string? catalogSource = null;
        if (info.SystemMaker == PcMaker.Lenovo && !info.IsSelfBuilt)
        {
            if (!settings.MakerCatalogs)
            {
                notes.Add("Lenovo's catalog check is off under Update sources, so the newest BIOS is not looked up.");
            }
            else if (LenovoCatalog.CatalogUrl(info.MachineType) is { } catalogUrl)
            {
                progress?.Report(new ProgressUpdate("Reading Lenovo's catalog for " + info.MachineType));
                (entries, var note) = await LenovoAsync(catalogUrl, online, forceRefresh, cancellationToken).ConfigureAwait(false);
                catalogSource = entries.Count > 0 ? "Lenovo's catalog" : null;
                if (note is not null)
                {
                    notes.Add(note);
                }
            }
            else
            {
                notes.Add("The Lenovo machine type could not be read, so Lenovo's catalog was not checked. Use the support page or Lenovo Vantage.");
            }
        }
        else if (info.SupportMaker is not (PcMaker.Unknown or PcMaker.Other))
        {
            var maker = SmbiosNormalizer.MakerName(info.SupportMaker);
            notes.Add($"{maker} does not publish a BIOS list PC Checker can read without an account, so compare the BIOS version on {maker}'s page.");
        }
        else
        {
            notes.Add("The PC or motherboard maker could not be recognised. Look up your model on the maker's site; System Information (msinfo32) shows the model.");
        }

        var bios = Bios(info, entries, support, catalogSource);
        return new PcPlatformReport(info, bios, chipset, support, entries, notes, DateTimeOffset.Now)
        {
            CheckedOnline = online,
            CatalogSource = catalogSource,
        };
    }

    /// <summary>The BIOS status: installed version and age, plus the catalog's newest when there is one.</summary>
    public BiosStatus Bios(PcPlatformInfo info, IReadOnlyList<MakerCatalogEntry> entries, PlatformLink? support, string? catalogSource)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(entries);
        var today = _today();
        var installed = BiosRules.PublishedForm(info.SupportMaker, info.BiosVersion);
        var latest = info.SystemMaker == PcMaker.Lenovo ? LenovoCatalog.BiosFor(entries, BiosRules.LenovoBiosId(info.BiosVersion)) : null;
        return new BiosStatus(installed, info.BiosDate, latest?.Version, latest?.ReleaseDate, latest is null ? null : catalogSource,
            latest is null ? DriverVersionComparison.Unknown : BiosRules.Compare(installed, latest.Version), support)
        {
            AgeYears = BiosRules.AgeYears(info.BiosDate, today),
            IsOld = BiosRules.IsOld(info.BiosDate, today),
        };
    }

    private async Task<ChipsetStatus?> ChipsetAsync(PcPlatformInfo info, string? installedAmd, PlatformLink? support, DriverSourceSettings settings,
        bool online, bool forceRefresh, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (info.Chipset == ChipsetPlatform.Intel)
        {
            return IntelChipset(info, support);
        }

        if (info.Chipset != ChipsetPlatform.Amd)
        {
            return null;
        }

        const string name = "AMD Chipset Software";
        var socketPage = AmdChipsetLookup.PageFor(info.Socket);
        var page = new PlatformLink(socketPage is null ? "AMD drivers and support" : $"AMD chipset drivers ({info.Socket})", socketPage ?? AmdChipsetLookup.GeneralPage,
            socketPage is null ? "Pick your processor on AMD's page." : "AMD's page for your socket.", socketPage is not null);
        var installedNote = installedAmd is null ? "AMD Chipset Software was not found in Apps." : $"You have {installedAmd}.";
        if (!settings.Chipset)
        {
            return new ChipsetStatus(ChipsetPlatform.Amd, name, installedAmd, null, null, DriverVersionComparison.Unknown, page)
            {
                Message = "The chipset check is off under Update sources. " + installedNote,
            };
        }

        if (socketPage is null)
        {
            var laptop = info.IsLaptop ? " On a laptop, the maker's chipset driver is often the safer choice." : string.Empty;
            return new ChipsetStatus(ChipsetPlatform.Amd, name, installedAmd, null, null, DriverVersionComparison.Unknown, page)
            {
                Message = "Your processor's socket is not one with an AMD chipset page, so the version could not be checked automatically. " + installedNote + laptop,
                MoreLinks = support is null ? [] : [support],
            };
        }

        progress?.Report(new ProgressUpdate("Asking AMD for the newest chipset driver"));
        CachedText? answer = null;
        string? problem = null;
        try
        {
            answer = await _cache.GetAsync(socketPage, online, forceRefresh,
                ct => _fetch(socketPage, AmdChipsetLookup.Referrer, AmdChipsetLookup.Hosts, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            problem = "AMD's chipset page could not be reached (" + ex.Message + "). Open the page to check. " + installedNote;
        }

        if (answer is null)
        {
            return new ChipsetStatus(ChipsetPlatform.Amd, name, installedAmd, null, null, DriverVersionComparison.Unknown, page)
            {
                Message = problem ?? "Not checked yet. Use 'Check for updates' to ask AMD. " + installedNote,
            };
        }

        var release = AmdChipsetLookup.ParsePage(answer.Text, socketPage);
        if (release is null)
        {
            return new ChipsetStatus(ChipsetPlatform.Amd, name, installedAmd, null, null, DriverVersionComparison.Unknown, page)
            {
                Message = "AMD's chipset page did not list the driver in the expected form (the page may have changed). Open the page to check. " + installedNote,
            };
        }

        var comparison = installedAmd is null ? DriverVersionComparison.Unknown : BiosRules.Compare(installedAmd, release.Version);
        var message = comparison switch
        {
            DriverVersionComparison.Newer => $"AMD has {release.Version}; you have {installedAmd}.",
            DriverVersionComparison.Same => $"Up to date ({installedAmd}).",
            DriverVersionComparison.Older => $"You have {installedAmd}, newer than AMD's page lists ({release.Version}).",
            _ => installedAmd is null
                ? $"AMD's newest is {release.Version}. AMD Chipset Software was not found in Apps; installing it gives Windows AMD's power and scheduler settings."
                : $"AMD's newest is {release.Version}; your version {installedAmd} could not be compared.",
        };
        if (answer.Stale)
        {
            message += " " + SavedAnswerNote("AMD", answer.FetchedAt, online);
        }

        return new ChipsetStatus(ChipsetPlatform.Amd, name, installedAmd, release.Version, release.ReleaseDate, comparison, page)
        {
            DownloadUrl = release.DownloadUrl,
            ReferrerUrl = socketPage,
            CheckedOnline = true,
            Message = message,
        };
    }

    /// <summary>Intel: no public version list, so the INF utility page and the Management Engine from the PC maker.</summary>
    public static ChipsetStatus IntelChipset(PcPlatformInfo info, PlatformLink? support)
    {
        ArgumentNullException.ThrowIfNull(info);
        var maker = SmbiosNormalizer.MakerName(info.SupportMaker);
        var more = new List<PlatformLink>();
        if (support is not null)
        {
            more.Add(support with { Title = $"Intel Management Engine driver from {maker}", Note = "Intel asks you to get the Management Engine driver from your PC or board maker." });
        }

        more.Add(new PlatformLink("Intel Driver & Support Assistant", IntelDriverCatalog.SupportAssistantPage, "Intel's own tool checks every Intel driver on this PC.", false));
        return new ChipsetStatus(ChipsetPlatform.Intel, "Intel Chipset INF Utility", null, null, null, DriverVersionComparison.Unknown,
            new PlatformLink("Intel Chipset INF Utility", IntelChipsetPage, "Intel's download page.", false))
        {
            Message = "Intel publishes no version list PC Checker can read, so check on Intel's site. The chipset INF files rarely change how a PC runs.",
            MoreLinks = more,
        };
    }

    private async Task<(IReadOnlyList<MakerCatalogEntry> Entries, string? Note)> LenovoAsync(string catalogUrl, bool online, bool forceRefresh, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> hosts = [LenovoCatalog.Host];
        CachedText? catalog;
        try
        {
            catalog = await _cache.GetAsync(catalogUrl, online, forceRefresh, ct => _fetch(catalogUrl, null, hosts, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return ([], "Lenovo has no public catalog for this model, so the newest BIOS and drivers are on the support page and in Lenovo Vantage.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return ([], "Lenovo's catalog could not be reached (" + ex.Message + "). Open the support page to check.");
        }

        if (catalog is null)
        {
            return ([], online ? null : "Use 'Check for updates' to read Lenovo's catalog for the newest BIOS.");
        }

        var packages = LenovoCatalog.PickPackages(LenovoCatalog.ParseCatalog(catalog.Text));
        var entries = new List<MakerCatalogEntry>();
        int failed = 0;
        using var gate = new SemaphoreSlim(4);
        var tasks = packages.Select(async p =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var text = await _cache.GetAsync(p.Location, online, forceRefresh, ct => _fetch(p.Location, null, hosts, ct), cancellationToken).ConfigureAwait(false);
                return text is null ? null : LenovoCatalog.ParsePackage(text.Text, p.Location, p.Category);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                Interlocked.Increment(ref failed);
                return null;
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        foreach (var entry in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        string? note = catalog.Stale ? SavedAnswerNote("Lenovo", catalog.FetchedAt, online) : null;
        if (failed > 0)
        {
            note = (note is null ? string.Empty : note + " ") + $"{failed} of Lenovo's package descriptions could not be read.";
        }

        return (entries, note);
    }

    /// <summary>"(Saved answer from 26/10/2026 14:05; AMD could not be reached just now.)" or, offline, how to ask again.</summary>
    public static string SavedAnswerNote(string maker, DateTimeOffset fetchedAt, bool triedOnline) =>
        $"(Saved answer from {fetchedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}; "
        + (triedOnline ? $"{maker} could not be reached just now.)" : "use 'Check for updates' to ask again.)");
}

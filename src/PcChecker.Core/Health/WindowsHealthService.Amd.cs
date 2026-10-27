using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Hardware;

namespace PcChecker.Core.Health;

/// <summary>
/// Hardware-error and AMD platform collectors of <see cref="WindowsHealthService"/>: WHEA-Logger events (the main
/// stability signal of a Ryzen PBO / Curve Optimizer / EXPO / FCLK setting), BugCheck 0x124, graphics driver timeouts
/// (Display 4101, LiveKernelEvent 141/117) and the AMD Chipset Software / 3D V-Cache checks. Event data is read by
/// field name or position from the event XML, so it works on every Windows language.
/// </summary>
public sealed partial class WindowsHealthService
{
    private const int MaxHardwareEvents = 500;

    private void CollectHardwareErrors(AnalysisContext ctx, CancellationToken ct)
    {
        long windowMs = HardwareErrorRules.WindowDays * 86_400_000L;
        string ids = string.Join(" or ", HardwareErrorRules.WheaEventIds.Select(i => $"EventID={i}"));
        var whea = new List<WheaEvent>();
        foreach (var record in Read("System", $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and ({ids}) and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]", ct))
        {
            var (named, _) = EventData(record.Xml);
            var parsed = HardwareErrorRules.ParseWhea(record.Id, record.Time, named);
            if (parsed.ErrorType is null && record.Description is { } text && text.Contains("Bus/Interconnect", StringComparison.OrdinalIgnoreCase))
            {
                parsed = parsed with { ErrorType = "Bus/Interconnect" };
            }

            whea.Add(parsed);
        }

        int bugCheck124 = 0;
        foreach (var record in Read("System", $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001 and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]", ct))
        {
            var (named, values) = EventData(record.Xml);
            var code = HardwareErrorRules.ParseBugCheckCode(named.GetValueOrDefault("param1") ?? values.FirstOrDefault());
            if (code == HardwareErrorRules.WheaUncorrectableBugCheck)
            {
                bugCheck124++;
            }
        }

        if (HardwareErrorRules.WheaItem(whea, bugCheck124, LocalCpu.Name, ctx.Now) is { } wheaItem)
        {
            ctx.Items.Add(wheaItem);
        }

        var timeouts = new List<GpuDriverTimeout>();
        foreach (var record in Read("System", $"*[System[Provider[@Name='Display'] and EventID=4101 and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]", ct))
        {
            var (_, values) = EventData(record.Xml);
            timeouts.Add(new GpuDriverTimeout(record.Time, HardwareErrorRules.DriverVendor(values) ?? "graphics", "Display 4101"));
        }

        foreach (var record in Read("Application", $"*[System[Provider[@Name='Windows Error Reporting'] and EventID=1001 and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]", ct))
        {
            var (_, values) = EventData(record.Xml);
            if (HardwareErrorRules.LiveKernelCode(values) is 0x141 or 0x117)
            {
                timeouts.Add(new GpuDriverTimeout(record.Time, HardwareErrorRules.DriverVendor(values) ?? GpuVendorName(), "LiveKernelEvent"));
            }
        }

        if (HardwareErrorRules.DriverTimeoutItem(timeouts, ctx.Now) is { } timeoutItem)
        {
            ctx.Items.Add(timeoutItem);
        }
    }

    private void CollectAmdPlatform(AnalysisContext ctx, CancellationToken ct)
    {
        var cpu = LocalCpu.Name;
        if (!CpuTraits.Parse(cpu).IsAmd)
        {
            return;
        }

        ctx.Items.AddRange(AmdPlatformRules.ChipsetItems(AmdPlatformProbe.Read(cpu, ct)));
    }

    private string GpuVendorName()
    {
        var gpu = _sensors.Latest?.Readings.FirstOrDefault(r => r.Hardware == Sensors.HardwareKind.Gpu && !Sensors.GpuSelection.IsIntegrated(r.HardwareName))?.HardwareName;
        return gpu is null ? "graphics" : Overclocking.GpuVendorDetector.DisplayName(Overclocking.GpuVendorDetector.FromName(gpu));
    }

    private sealed record RawEvent(int Id, DateTimeOffset Time, string? Xml, string? Description);

    private List<RawEvent> Read(string log, string xpath, CancellationToken ct)
    {
        var list = new List<RawEvent>();
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (EventRecord? record = reader.ReadEvent(); record is not null && list.Count < MaxHardwareEvents; record = reader.ReadEvent())
            {
                ct.ThrowIfCancellationRequested();
                using (record)
                {
                    string? xml = null;
                    string? text = null;
                    try
                    {
                        xml = record.ToXml();
                        text = record.FormatDescription();
                    }
                    catch (Exception)
                    {
                        // Missing message DLL: the XML (or nothing) is enough.
                    }

                    list.Add(new RawEvent(record.Id, record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.Now, xml, text));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EventLogNotFoundException)
        {
            // Provider or log absent: nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hardware error query on {Log} failed.", log);
        }

        return list;
    }

    /// <summary>Named and positional &lt;Data&gt; values of an event's XML.</summary>
    internal static (Dictionary<string, string?> Named, List<string?> Values) EventData(string? xml)
    {
        var named = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var values = new List<string?>();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return (named, values);
        }

        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var data in doc.Descendants().Where(e => e.Name.LocalName == "Data"))
            {
                values.Add(data.Value);
                if (data.Attribute("Name")?.Value is { Length: > 0 } name)
                {
                    named[name] = data.Value;
                }
            }
        }
        catch (Exception)
        {
            // Malformed XML: treat as no data.
        }

        return (named, values);
    }
}

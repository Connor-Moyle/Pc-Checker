using System.Text.RegularExpressions;
using PcChecker.Core.Common;

namespace PcChecker.Core.Drivers;

/// <summary>Parsers for <c>pnputil /enum-devices</c> and a shared mapping of a pnputil run to an <see cref="OperationResult"/>.</summary>
public static partial class PnpUtilParser
{
    /// <summary>A device instance id: an enumerator, a backslash and more ("USB\VID_0FD9&amp;PID_004F\5&amp;1A2B").</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_&\-\.]+\\\S")]
    private static partial Regex InstanceIdShape();

    /// <summary>
    /// Parses <c>pnputil /enum-devices</c> output (used with <c>/disconnected</c>): blank-line separated blocks of
    /// "Label: Value" lines. English labels are used when present; with translated labels the fields are found by the
    /// shape of their values and pnputil's fixed print order: Instance ID, Device Description, Class Name, Class GUID,
    /// Manufacturer Name, Status, Driver Name. Blocks without an instance id are skipped.
    /// </summary>
    public static IReadOnlyList<DisconnectedDevice> ParseEnumDevices(string? output)
    {
        var devices = new List<DisconnectedDevice>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return devices;
        }

        var entries = new List<(string Key, string Value)>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            var m = KeyValueLine().Match(line);
            if (m.Success)
            {
                entries.Add((m.Groups[1].Value, m.Groups[2].Value.Trim()));
            }
        }

        Flush();
        return devices;

        void Flush()
        {
            if (entries.Count > 0)
            {
                var block = MapDeviceFields(entries);
                if (block.TryGetValue("instanceid", out var id) && !string.IsNullOrWhiteSpace(id))
                {
                    devices.Add(new DisconnectedDevice(
                        id,
                        NullIfEmpty(block.GetValueOrDefault("devicedescription")),
                        NullIfEmpty(block.GetValueOrDefault("classname")),
                        NullIfEmpty(block.GetValueOrDefault("classguid")),
                        NullIfEmpty(block.GetValueOrDefault("manufacturername")),
                        NullIfEmpty(block.GetValueOrDefault("status")),
                        NullIfEmpty(block.GetValueOrDefault("drivername"))));
                }
            }

            entries.Clear();
        }
    }

    /// <summary>
    /// Turns a finished pnputil run into an <see cref="OperationResult"/>: start and timeout failures, then the exit
    /// code through <see cref="InterpretExitCode"/> (reboot flags included), with pnputil's last line on failure.
    /// </summary>
    public static OperationResult ToOperationResult(PnpUtilResult result, string successMessage)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Started)
        {
            return OperationResult.Fail("pnputil could not start: " + result.StartError);
        }

        if (result.TimedOut)
        {
            return OperationResult.Fail("pnputil took too long and was stopped.");
        }

        var (success, reboot, text) = InterpretExitCode(result.ExitCode);
        if (success)
        {
            return OperationResult.Ok(reboot ? successMessage + " Restart Windows to finish." : successMessage, reboot);
        }

        var tail = result.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("Microsoft PnP", StringComparison.OrdinalIgnoreCase));
        return OperationResult.Fail(tail is null ? text : text + " " + tail);
    }

    private static Dictionary<string, string> MapDeviceFields(List<(string Key, string Value)> entries)
    {
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            byKey.TryAdd(Normalize(key), value);
        }

        if (byKey.ContainsKey("instanceid"))
        {
            return byKey;
        }

        int instance = entries.FindIndex(e => InstanceIdShape().IsMatch(e.Value) && !ClassGuid().IsMatch(e.Value));
        if (instance < 0)
        {
            return byKey;
        }

        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["instanceid"] = entries[instance].Value };
        int guid = entries.FindIndex(instance + 1, e => ClassGuid().IsMatch(e.Value));
        int driver = entries.FindLastIndex(e => IsInfName(e.Value));
        if (guid >= 0)
        {
            mapped["classguid"] = entries[guid].Value;
        }

        if (driver > instance)
        {
            mapped["drivername"] = entries[driver].Value;
        }

        // Description and class name sit between the instance id and the class GUID.
        int end = guid >= 0 ? guid : driver > instance ? driver : entries.Count;
        if (end - instance > 1)
        {
            mapped["devicedescription"] = entries[instance + 1].Value;
        }

        if (end - instance > 2)
        {
            mapped["classname"] = entries[instance + 2].Value;
        }

        // Manufacturer and status follow the class GUID, before the driver name.
        if (guid >= 0)
        {
            int stop = driver > guid ? driver : entries.Count;
            if (stop - guid > 1)
            {
                mapped["manufacturername"] = entries[guid + 1].Value;
            }

            if (stop - guid > 2)
            {
                mapped["status"] = entries[guid + 2].Value;
            }
        }

        return mapped;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

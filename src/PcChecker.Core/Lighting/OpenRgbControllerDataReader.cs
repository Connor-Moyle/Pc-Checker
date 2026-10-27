using System.Buffers.Binary;
using System.Text;
using OpenRGB.NET;

namespace PcChecker.Core.Lighting;

/// <summary>One mode as the SDK server sends it.</summary>
public sealed record OpenRgbModeData(
    string Name,
    int Value,
    uint Flags,
    uint SpeedMin,
    uint SpeedMax,
    uint BrightnessMin,
    uint BrightnessMax,
    uint ColorsMin,
    uint ColorsMax,
    uint Speed,
    uint Brightness,
    uint Direction,
    uint ColorMode,
    IReadOnlyList<RgbColor> Colors);

/// <summary>One segment of a zone (protocol 4 and later).</summary>
public sealed record OpenRgbSegmentData(string Name, int Type, uint StartIndex, uint LedCount);

/// <summary>One zone as the SDK server sends it.</summary>
public sealed record OpenRgbZoneData(
    string Name,
    int Type,
    uint LedsMin,
    uint LedsMax,
    uint LedCount,
    uint MatrixHeight,
    uint MatrixWidth,
    IReadOnlyList<OpenRgbSegmentData> Segments,
    uint Flags);

/// <summary>A whole controller (RGBController) as the SDK server sends it in reply to REQUEST_CONTROLLER_DATA.</summary>
public sealed record OpenRgbControllerData(
    int ProtocolVersion,
    int Type,
    string Name,
    string Vendor,
    string Description,
    string Version,
    string Serial,
    string Location,
    int ActiveMode,
    IReadOnlyList<OpenRgbModeData> Modes,
    IReadOnlyList<OpenRgbZoneData> Zones,
    IReadOnlyList<string> LedNames,
    IReadOnlyList<RgbColor> Colors,
    uint Flags);

/// <summary>
/// Defensive reader for the OpenRGB SDK "controller data" block (documented in OpenRGB's Documentation/OpenRGBSDK.md),
/// protocol versions 0 to 5. Every length is checked against what is left, absurd counts are refused, unknown device
/// types become "Other", and bytes after the known fields (added by newer protocol versions) are ignored, so one odd
/// device yields a clear error instead of a crash or a misaligned list. Pure: used by <see cref="OpenRgbSdkProbe"/> and
/// unit-tested with byte samples.
/// </summary>
public static class OpenRgbControllerDataReader
{
    /// <summary>The newest protocol this reader understands. Version 6 drops fields and changes the device list, so it is not requested.</summary>
    public const int MaxProtocolVersion = 5;

    private const int MaxItems = 4096;
    private const int MaxLeds = 1 << 16;

    /// <summary>
    /// Parses a REQUEST_CONTROLLER_DATA reply payload (starting with the 4-byte data_size) read with <paramref name="protocolVersion"/>.
    /// Returns null and a reason when the data is short or inconsistent.
    /// </summary>
    public static OpenRgbControllerData? TryRead(ReadOnlySpan<byte> payload, int protocolVersion, out string? error)
    {
        error = null;
        if (protocolVersion is < 0 or > MaxProtocolVersion)
        {
            error = $"protocol version {protocolVersion} is not supported (0 to {MaxProtocolVersion}).";
            return null;
        }

        var r = new Reader(payload);
        try
        {
            // data_size: not trusted for slicing (servers differ on whether it counts itself); every field is bounds-checked instead.
            r.U32();
            int type = r.I32();
            string name = r.Str();
            string vendor = protocolVersion >= 1 ? r.Str() : string.Empty;
            string description = r.Str();
            string version = r.Str();
            string serial = r.Str();
            string location = r.Str();

            int modeCount = r.Count();
            int activeMode = r.I32();
            var modes = new List<OpenRgbModeData>(modeCount);
            for (int i = 0; i < modeCount; i++)
            {
                modes.Add(ReadMode(ref r, protocolVersion));
            }

            int zoneCount = r.Count();
            var zones = new List<OpenRgbZoneData>(zoneCount);
            for (int i = 0; i < zoneCount; i++)
            {
                zones.Add(ReadZone(ref r, protocolVersion));
            }

            int ledCount = r.Count(MaxLeds);
            var ledNames = new List<string>(ledCount);
            for (int i = 0; i < ledCount; i++)
            {
                ledNames.Add(r.Str());
                r.I32(); // led_value: internal to OpenRGB
            }

            int colorCount = r.Count(MaxLeds);
            var colors = new List<RgbColor>(colorCount);
            for (int i = 0; i < colorCount; i++)
            {
                colors.Add(r.Color());
            }

            uint flags = 0;
            if (protocolVersion >= 5)
            {
                int alt = r.Count(MaxLeds);
                for (int i = 0; i < alt; i++)
                {
                    r.Str();
                }

                flags = r.U32();
            }

            return new OpenRgbControllerData(protocolVersion, type, name, vendor, description, version, serial, location,
                activeMode, modes, zones, ledNames, colors, flags);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Converts parsed controller data to the app's device model (same rules as <see cref="OpenRgbDeviceMapper"/>).</summary>
    public static RgbDevice ToDevice(OpenRgbControllerData data, int index)
    {
        ArgumentNullException.ThrowIfNull(data);
        var modes = data.Modes.Select((m, i) => OpenRgbDeviceMapper.MapMode(i, m.Name, (ModeFlags)m.Flags, m.SpeedMin, m.SpeedMax)).ToList();
        var zones = data.Zones.Select((z, i) => OpenRgbDeviceMapper.MapZone(i, z.Name, z.LedCount, z.LedsMin, z.LedsMax)).ToList();
        string type = OpenRgbDeviceTypes.Refine(OpenRgbDeviceTypes.FriendlyName(data.Type), data.Name);
        int active = data.ActiveMode >= 0 && data.ActiveMode < modes.Count ? data.ActiveMode : 0;
        return new RgbDevice(
            index,
            string.IsNullOrWhiteSpace(data.Name) ? $"Device {index + 1}" : data.Name.Trim(),
            string.IsNullOrWhiteSpace(data.Vendor) ? OpenRgbDeviceMapper.GuessVendor(data.Name) : data.Vendor.Trim(),
            type,
            string.IsNullOrWhiteSpace(data.Serial) ? null : data.Serial.Trim(),
            data.Colors.Count > 0 ? data.Colors.Count : data.LedNames.Count,
            zones,
            modes,
            active,
            data.Colors.Count > 0 ? data.Colors[0] : null,
            string.IsNullOrWhiteSpace(data.Location) ? null : data.Location.Trim());
    }

    private static OpenRgbModeData ReadMode(ref Reader r, int protocol)
    {
        string name = r.Str();
        int value = r.I32();
        uint flags = r.U32();
        uint speedMin = r.U32();
        uint speedMax = r.U32();
        uint brightMin = 0, brightMax = 0;
        if (protocol >= 3)
        {
            brightMin = r.U32();
            brightMax = r.U32();
        }

        uint colorsMin = r.U32();
        uint colorsMax = r.U32();
        uint speed = r.U32();
        uint brightness = protocol >= 3 ? r.U32() : 0;
        uint direction = r.U32();
        uint colorMode = r.U32();
        int colorCount = r.Count();
        var colors = new List<RgbColor>(colorCount);
        for (int i = 0; i < colorCount; i++)
        {
            colors.Add(r.Color());
        }

        return new OpenRgbModeData(name, value, flags, speedMin, speedMax, brightMin, brightMax, colorsMin, colorsMax, speed, brightness, direction, colorMode, colors);
    }

    private static OpenRgbZoneData ReadZone(ref Reader r, int protocol)
    {
        string name = r.Str();
        int type = r.I32();
        uint ledsMin = r.U32();
        uint ledsMax = r.U32();
        uint ledCount = r.U32();
        if (ledCount > MaxLeds)
        {
            throw new FormatException($"zone \"{name}\" reports {ledCount} LEDs, which cannot be right.");
        }

        int matrixLength = r.U16();
        uint height = 0, width = 0;
        if (matrixLength > 0)
        {
            if (matrixLength < 8)
            {
                throw new FormatException($"zone \"{name}\" has a matrix block of {matrixLength} bytes (at least 8 expected).");
            }

            int start = r.Position;
            height = r.U32();
            width = r.U32();
            // Skip the map by its stated length, not by height × width: the length is what the server actually wrote.
            r.Skip(matrixLength - (r.Position - start));
        }

        var segments = new List<OpenRgbSegmentData>();
        if (protocol >= 4)
        {
            int segmentCount = r.Count();
            for (int i = 0; i < segmentCount; i++)
            {
                segments.Add(new OpenRgbSegmentData(r.Str(), r.I32(), r.U32(), r.U32()));
            }
        }

        uint flags = protocol >= 5 ? r.U32() : 0;
        return new OpenRgbZoneData(name, type, ledsMin, ledsMax, ledCount, height, width, segments, flags);
    }

    /// <summary>Little-endian reader that throws <see cref="FormatException"/> with the offset when the data runs out.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            Position = 0;
        }

        public int Position { get; set; }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Position + count > _data.Length)
            {
                throw new FormatException($"the data ends early (needed {count} more bytes at offset {Position} of {_data.Length}).");
            }

            var slice = _data.Slice(Position, count);
            Position += count;
            return slice;
        }

        public void Skip(int count) => Take(count);

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public int Count(int max = MaxItems)
        {
            int count = U16();
            if (count > max)
            {
                throw new FormatException($"a list of {count} items at offset {Position - 2} is larger than any real device.");
            }

            return count;
        }

        /// <summary>Length-prefixed, null-terminated string (the length includes the terminator).</summary>
        public string Str()
        {
            int length = U16();
            var bytes = Take(length);
            int end = bytes.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes);
        }

        /// <summary>RGBColor: red, green, blue and one unused byte.</summary>
        public RgbColor Color()
        {
            var b = Take(4);
            return new RgbColor(b[0], b[1], b[2]);
        }
    }
}

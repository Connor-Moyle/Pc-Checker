using System.Security.Cryptography;

namespace PcChecker.Core.Hosts;

/// <summary>Turns the bytes of a hosts file into a <see cref="HostsSnapshot"/> (shared by the Windows and demo services).</summary>
public static class HostsSnapshotBuilder
{
    public static HostsSnapshot Build(string path, byte[]? bytes, HostsFileFacts facts, int ansiCodePage = HostsEncodingDetector.DefaultAnsiCodePage,
        IEnumerable<string>? extraWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var data = bytes ?? [];
        var encoding = data.Length == 0 ? HostsEncoding.Utf8 : HostsEncodingDetector.Detect(data);
        string text = HostsEncodingDetector.Decode(data, encoding, ansiCodePage);
        var warnings = HostsFileRules.Warnings(facts).Concat(extraWarnings ?? []).ToList();
        return new HostsSnapshot(path, HostsParser.Parse(text), text, encoding, facts, warnings, Hash(bytes), DateTimeOffset.Now);
    }

    /// <summary>SHA-256 of the file, or an empty string when there is no file.</summary>
    public static string Hash(byte[]? bytes) => bytes is null ? string.Empty : Convert.ToHexString(SHA256.HashData(bytes));
}

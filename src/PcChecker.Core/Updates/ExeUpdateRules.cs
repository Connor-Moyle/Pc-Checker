using System.Text.RegularExpressions;

namespace PcChecker.Core.Updates;

/// <summary>
/// Pure rules for the single-exe self-updater: which release asset is the app, which SHA-256 it must match, and
/// where the old and new files go during the swap.
/// </summary>
public static partial class ExeUpdateRules
{
    /// <summary>The file name every release ships. The updater looks for it first.</summary>
    public const string AssetName = "PcChecker.exe";

    /// <summary>Picks the app executable from a release: <see cref="AssetName"/>, else the only .exe, else null.</summary>
    public static GitHubReleaseAsset? FindExeAsset(GitHubRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var exact = release.Assets.FirstOrDefault(a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var exes = release.Assets.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        return exes.Count == 1 ? exes[0] : null;
    }

    /// <summary>
    /// The expected SHA-256 (lower-case hex): GitHub's asset digest when present, otherwise a "SHA-256" line in the
    /// release notes. Null when neither is available, in which case the update is refused.
    /// </summary>
    public static string? ExpectedSha256(GitHubReleaseAsset asset, string? releaseBody)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!string.IsNullOrWhiteSpace(asset.Digest))
        {
            var digest = asset.Digest.Trim();
            if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                digest = digest[7..];
            }

            if (Sha256Hex().IsMatch(digest) && digest.Length == 64)
            {
                return digest.ToLowerInvariant();
            }
        }

        if (string.IsNullOrWhiteSpace(releaseBody))
        {
            return null;
        }

        var match = BodySha256().Match(releaseBody);
        return match.Success ? match.Groups["hash"].Value.ToLowerInvariant() : null;
    }

    /// <summary>Path the running executable is moved to while the new one takes its place.</summary>
    public static string OldFilePath(string currentExePath) => currentExePath + ".old";

    /// <summary>Path the download is written to before it is verified.</summary>
    public static string DownloadFilePath(string updatesFolder, string version) =>
        Path.Combine(updatesFolder, $"PcChecker-{SafeVersion(version)}.exe");

    /// <summary>True when the running process is a published PcChecker executable that can replace itself.</summary>
    public static bool CanSelfUpdate(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        // Split on both separators so the rule behaves the same whichever OS evaluates it.
        var name = processPath[(processPath.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            && name.StartsWith("PcChecker", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeVersion(string version)
    {
        var chars = (version ?? "0").Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray();
        return chars.Length == 0 ? "0" : new string(chars);
    }

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex Sha256Hex();

    [GeneratedRegex(@"SHA-?256.{0,80}?\b(?<hash>[0-9a-fA-F]{64})\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodySha256();
}

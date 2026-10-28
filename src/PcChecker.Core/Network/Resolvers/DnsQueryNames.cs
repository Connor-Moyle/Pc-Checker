namespace PcChecker.Core.Network.Resolvers;

/// <summary>
/// The names the benchmark looks up. Popular names are almost always in a resolver's cache, so they measure how quickly
/// it answers; made-up names under a few big domains cannot be cached, so they measure how quickly it finds an answer
/// it does not have yet. Pure.
/// </summary>
public static class DnsQueryNames
{
    /// <summary>Names nearly every resolver has cached (the sites people and games use).</summary>
    public static IReadOnlyList<string> Popular { get; } =
    [
        "google.com", "youtube.com", "microsoft.com", "steamcontent.com",
        "cloudflare.com", "amazon.com", "discord.com", "riotgames.com",
    ];

    /// <summary>Domains under which random names are made up for the uncached test.</summary>
    public static IReadOnlyList<string> UncachedBases { get; } = ["microsoft.com", "amazon.com", "wikipedia.org", "github.com"];

    /// <summary>A signed domain: a resolver that checks DNSSEC sets the "authenticated" flag on its answer.</summary>
    public const string DnssecProbeName = "cloudflare.com";

    /// <summary>Every random label starts with this, so the lookups are easy to recognise in a DNS log.</summary>
    public const string RandomPrefix = "pcc-";

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A random lower-case label of <paramref name="length"/> letters and digits.</summary>
    public static string RandomLabel(Random random, int length = 12)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (length is < 1 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "A DNS label has 1 to 63 characters.");
        }

        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = Alphabet[random.Next(Alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>"pcc-k2v9x0q1m3za.amazon.com": a name no resolver can have cached.</summary>
    public static string UncachedName(Random random, string baseDomain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDomain);
        return $"{RandomPrefix}{RandomLabel(random)}.{baseDomain.Trim().TrimEnd('.').ToLowerInvariant()}";
    }

    /// <summary><paramref name="count"/> uncached names spread over <see cref="UncachedBases"/> in turn.</summary>
    public static IReadOnlyList<string> UncachedNames(Random random, int count, IReadOnlyList<string>? bases = null)
    {
        bases ??= UncachedBases;
        if (bases.Count == 0 || count <= 0)
        {
            return [];
        }

        var names = new List<string>(count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (names.Count < count)
        {
            string name = UncachedName(random, bases[names.Count % bases.Count]);
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>True when <paramref name="name"/> could be sent in a query (labels of 1 to 63 printable ASCII characters).</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            DnsQueryBuilder.EncodeName(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

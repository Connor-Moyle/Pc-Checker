using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PcChecker.Core.Drivers.Platform;

/// <summary>
/// Keeps each maker answer (AMD's chipset page, Lenovo's catalog) for 12 hours so a second search does not ask
/// again, and keeps the last answer after that so an offline PC still shows what was found, marked as saved. Kept
/// in memory and, when a folder is given, as small files that survive a restart. Every file access is guarded: a
/// missing or damaged file just means no saved answer.
/// </summary>
public sealed class PlatformLookupCache
{
    /// <summary>How long an answer is used without asking again.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(12);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly ConcurrentDictionary<string, Entry> _memory = new(StringComparer.Ordinal);
    private readonly string? _folder;
    private readonly Func<DateTimeOffset> _now;

    public PlatformLookupCache(string? folder = null, Func<DateTimeOffset>? now = null)
    {
        _folder = folder;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>The default folder: %LOCALAPPDATA%\PcChecker\cache\driver-lookups.</summary>
    public static string? DefaultFolder()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "PcChecker", "cache", "driver-lookups");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True while an answer fetched at <paramref name="fetchedAt"/> is younger than <see cref="FreshFor"/>.</summary>
    public static bool IsFresh(DateTimeOffset fetchedAt, DateTimeOffset now) => fetchedAt <= now && now - fetchedAt < FreshFor;

    /// <summary>
    /// The answer for <paramref name="url"/>: a fresh saved one, else a new one from <paramref name="fetch"/> when
    /// <paramref name="online"/>, else the last saved one marked stale. When fetching fails, the last saved answer is
    /// returned (stale) if there is one; otherwise the error is thrown. Null only when offline with nothing saved.
    /// </summary>
    public async Task<CachedText?> GetAsync(string url, bool online, bool forceRefresh, Func<CancellationToken, Task<string>> fetch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(fetch);
        var now = _now();
        var saved = Read(url);
        if (saved is not null && !forceRefresh && IsFresh(saved.FetchedAt, now))
        {
            return new CachedText(saved.Text, saved.FetchedAt, true, false);
        }

        if (!online)
        {
            return saved is null ? null : new CachedText(saved.Text, saved.FetchedAt, true, !IsFresh(saved.FetchedAt, now));
        }

        try
        {
            var text = await fetch(cancellationToken).ConfigureAwait(false);
            Write(url, new Entry(url, now, text));
            return new CachedText(text, now, false, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException && saved is not null)
        {
            return new CachedText(saved!.Text, saved.FetchedAt, true, true);
        }
    }

    /// <summary>Forgets every saved answer (memory and files).</summary>
    public void Clear()
    {
        _memory.Clear();
        if (_folder is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_folder))
            {
                foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // A cache that cannot be cleared only means an older answer is reused once more.
        }
    }

    private Entry? Read(string url)
    {
        if (_memory.TryGetValue(url, out var entry))
        {
            return entry;
        }

        if (_folder is null)
        {
            return null;
        }

        try
        {
            var path = PathFor(url);
            if (!File.Exists(path))
            {
                return null;
            }

            var loaded = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Json);
            if (loaded is null || loaded.Url != url || loaded.Text is null)
            {
                return null;
            }

            _memory[url] = loaded;
            return loaded;
        }
        catch
        {
            return null;
        }
    }

    private void Write(string url, Entry entry)
    {
        _memory[url] = entry;
        if (_folder is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_folder);
            var path = PathFor(url);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entry, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // The cache only saves a request; losing it is harmless.
        }
    }

    /// <summary>The file for a URL: a hash, so no part of the URL ends up in a file name.</summary>
    private string PathFor(string url) =>
        Path.Combine(_folder!, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32].ToLowerInvariant() + ".json");

    private sealed record Entry(string Url, DateTimeOffset FetchedAt, string Text);
}

using System.Collections.Concurrent;
using System.Net;

namespace PcChecker.Core.Connections;

/// <summary>
/// Remote host names for the Connections page: each address is looked up once in the background (a few at a time),
/// answers and failures are both remembered, and <see cref="Resolved"/> tells the page to refresh. Loopback and
/// "any" addresses are never looked up. The lookup itself is passed in, so the cache is unit-tested.
/// </summary>
public sealed class HostNameCache
{
    private readonly Func<IPAddress, CancellationToken, Task<string?>> _lookup;
    private readonly ConcurrentDictionary<IPAddress, string?> _names = new();
    private readonly ConcurrentDictionary<IPAddress, byte> _pending = new();
    private readonly SemaphoreSlim _slots;
    private readonly int _maxEntries;

    public HostNameCache(Func<IPAddress, CancellationToken, Task<string?>> lookup, int maxConcurrent = 4, int maxEntries = 4096)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrent));
        _maxEntries = Math.Max(16, maxEntries);
    }

    /// <summary>Raised (on a worker thread) when a lookup has finished with a name.</summary>
    public event EventHandler? Resolved;

    /// <summary>Lookups that have finished, with or without a name.</summary>
    public int Count => _names.Count;

    /// <summary>
    /// The cached name of <paramref name="address"/>, or null. With <paramref name="startLookup"/>, an address not
    /// seen yet is looked up in the background.
    /// </summary>
    public string? Get(IPAddress? address, bool startLookup, CancellationToken cancellationToken = default)
    {
        if (address is null || ConnectionRules.IsAnyAddress(address) || ConnectionRules.IsLoopback(address))
        {
            return null;
        }

        if (_names.TryGetValue(address, out var name))
        {
            return name;
        }

        if (startLookup && _names.Count < _maxEntries && _pending.TryAdd(address, 0))
        {
            _ = LookupAsync(address, cancellationToken);
        }

        return null;
    }

    private async Task LookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        string? name = null;
        bool entered = false;
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            name = Clean(await _lookup(address, cancellationToken).ConfigureAwait(false), address);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(address, out _);
            return;
        }
        catch (Exception)
        {
            // No reverse name (or no DNS server): remember that, so it is not asked again.
        }
        finally
        {
            if (entered)
            {
                _slots.Release();
            }
        }

        _names[address] = name;
        _pending.TryRemove(address, out _);
        if (name is not null)
        {
            Resolved?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A name that is only the address again is no name.</summary>
    public static string? Clean(string? name, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim().TrimEnd('.');
        return trimmed.Length == 0 || IPAddress.TryParse(trimmed, out _) || trimmed.Equals(address.ToString(), StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }
}

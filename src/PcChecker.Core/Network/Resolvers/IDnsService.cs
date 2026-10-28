using PcChecker.Core.Common;

namespace PcChecker.Core.Network.Resolvers;

/// <summary>Whether Windows sends an adapter's DNS lookups encrypted (DNS over HTTPS).</summary>
public enum DnsEncryptionState
{
    Unknown,

    /// <summary>Windows 10 and older: no built-in DNS over HTTPS.</summary>
    NotSupported,
    Off,
    Partly,
    On,
}

/// <summary>
/// One connected adapter's DNS. <see cref="Servers4"/> and <see cref="Servers6"/> are what Windows uses now;
/// <see cref="ManualServers4"/>/<see cref="ManualServers6"/> are the servers typed in by hand (empty = automatic, from the
/// router; null = could not be read); <see cref="RouterServers"/> are the ones the router hands out (DHCP).
/// </summary>
public sealed record DnsAdapterInfo(
    string Id,
    int Index,
    string Name,
    string Description,
    bool IsWifi,
    IReadOnlyList<string> Servers4,
    IReadOnlyList<string> Servers6,
    string ManualServers4,
    string? ManualServers6,
    IReadOnlyList<string> RouterServers,
    string? Gateway,
    DnsEncryptionState Encryption)
{
    public bool IsAutomatic4 => string.IsNullOrWhiteSpace(ManualServers4);

    public bool IsAutomatic6 => string.IsNullOrWhiteSpace(ManualServers6);

    /// <summary>Every server in use, IPv4 first.</summary>
    public IReadOnlyList<string> AllServers => [.. Servers4, .. Servers6];
}

/// <summary>The DNS of every connected adapter.</summary>
public sealed record DnsStatus(IReadOnlyList<DnsAdapterInfo> Adapters, bool EncryptionSupported, DateTimeOffset ReadAt)
{
    public static DnsStatus Empty { get; } = new([], false, DateTimeOffset.MinValue);
}

/// <summary>
/// A switch: which adapters, which servers (IPv6 may be empty to leave IPv6 as it is) and whether to turn on DNS over
/// HTTPS for the servers that offer it. <see cref="Label"/> names the choice in messages and the journal.
/// </summary>
public sealed record DnsChangeRequest(
    IReadOnlyList<string> AdapterIds,
    string Label,
    IReadOnlyList<string> Servers4,
    IReadOnlyList<string> Servers6,
    bool EncryptWithDoh);

/// <summary>
/// The DNS page: current servers per adapter, a benchmark of real DNS lookups, switching servers with an exact undo,
/// and going back to automatic. Every change asks the change guard first and saves what it replaces.
/// </summary>
public interface IDnsService
{
    Task<DnsStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Times real lookups against <paramref name="targets"/>. Stops after about 30 seconds.</summary>
    Task<DnsBenchmarkReport> BenchmarkAsync(IReadOnlyList<DnsBenchmarkTarget> targets, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Points the chosen adapters at new servers and clears the DNS cache. What was there before is saved first.</summary>
    Task<OperationResult> SwitchAsync(DnsChangeRequest request, CancellationToken cancellationToken);

    /// <summary>Lets the router hand out the DNS servers again (IPv4 and IPv6) and clears the DNS cache.</summary>
    Task<OperationResult> ResetToAutomaticAsync(IReadOnlyList<string> adapterIds, CancellationToken cancellationToken);

    /// <summary>Puts back exactly what the last switch or reset replaced, including "automatic" and DNS over HTTPS.</summary>
    Task<OperationResult> UndoLastChangeAsync(CancellationToken cancellationToken);

    /// <summary>What the last change replaced, or null when there is nothing to undo.</summary>
    DnsPreviousSettings? LastChange { get; }
}

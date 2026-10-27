namespace PcChecker.Core.Debloat;

/// <summary>A registry value in canonical text form (see <see cref="RegistryValueCodec"/>).</summary>
public sealed record RegistryData(DebloatValueKind Kind, string Value);

/// <summary>
/// The registry operations the debloat engine needs. The Windows implementation uses the 64-bit view; tests and the
/// demo use an in-memory implementation, so state detection and capture/revert run anywhere.
/// </summary>
public interface IRegistryAccessor
{
    /// <summary>Returns the value, or null when the key or value does not exist (or cannot be read).</summary>
    RegistryData? Read(DebloatHive hive, string path, string name);

    bool KeyExists(DebloatHive hive, string path);

    IReadOnlyList<string> GetSubKeyNames(DebloatHive hive, string path);

    /// <summary>Creates the key when needed and writes the value. Throws on failure (access denied, blocked by policy).</summary>
    void Write(DebloatHive hive, string path, string name, RegistryData data);

    /// <summary>Deletes a value; missing values are ignored. Throws on failure.</summary>
    void DeleteValue(DebloatHive hive, string path, string name);

    /// <summary>Deletes a key and everything under it; a missing key is ignored. Throws on failure.</summary>
    void DeleteKeyTree(DebloatHive hive, string path);
}

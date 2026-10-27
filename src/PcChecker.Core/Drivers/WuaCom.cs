namespace PcChecker.Core.Drivers;

/// <summary>
/// Late-binding helpers shared by the Windows Update Agent wrappers (driver updates here, software updates in
/// <c>WindowsUpdate</c>): create a COM object by ProgID and read or write optional properties without throwing.
/// </summary>
internal static class WuaCom
{
    /// <summary>The client name Windows Update records for searches, downloads and installs started by PC Checker.</summary>
    public const string ClientId = "PC Checker";

    /// <summary><c>ServerSelection.ssDefault</c>: the server Windows is configured to use (an organisation's WSUS when set).</summary>
    public const int ServerSelectionDefault = 0;

    /// <summary><c>ServerSelection.ssOthers</c>: the service named by <c>ServiceID</c>.</summary>
    public const int ServerSelectionOthers = 3;

    public static object CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false)
            ?? throw new InvalidOperationException($"COM class {progId} is not registered on this computer.");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException($"COM class {progId} could not be created.");
    }

    public static T TryGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    public static void TrySet(Action setter)
    {
        try
        {
            setter();
        }
        catch
        {
            // Optional property on this WUA version.
        }
    }
}

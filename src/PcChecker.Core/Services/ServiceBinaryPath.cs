using PcChecker.Core.Performance;

namespace PcChecker.Core.Services;

/// <summary>Pure helpers for a service's binary path and the "search online" link. No Windows calls.</summary>
public static class ServiceBinaryPath
{
    /// <summary>The executable path out of a service command line (strips quotes, arguments and <c>svchost -k</c>), or null.</summary>
    public static string? ExtractExecutable(string? binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        var exe = ServiceClassifier.ExtractExecutable(binaryPath);
        return string.IsNullOrWhiteSpace(exe) ? null : exe;
    }

    /// <summary>True when the service binary belongs to Windows or a Microsoft product.</summary>
    public static bool IsMicrosoft(string? binaryPath) => ServiceClassifier.IsMicrosoft(binaryPath);

    /// <summary>A privacy-friendly web search for a service name, so people can look up what one does.</summary>
    public static string SearchUrl(string? serviceName, string? displayName)
    {
        var query = (string.IsNullOrWhiteSpace(displayName) ? serviceName : displayName)?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            query = serviceName ?? "windows service";
        }

        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query + " windows service");
    }
}

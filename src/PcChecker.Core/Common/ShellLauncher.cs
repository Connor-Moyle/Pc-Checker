using System.Diagnostics;

namespace PcChecker.Core.Common;

/// <summary>Opens a URI, settings page or executable with the shell; never throws.</summary>
public static class ShellLauncher
{
    public static OperationResult Open(string target, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return OperationResult.Fail("Nothing to open.");
        }

        if (!PlatformGuard.IsWindows)
        {
            return OperationResult.Fail("Opening Windows tools is only possible on Windows.");
        }

        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                psi.Arguments = arguments;
            }

            Process.Start(psi);
            return OperationResult.Ok($"Opened {target}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"Could not open {target}: {ex.Message}", ex);
        }
    }
}

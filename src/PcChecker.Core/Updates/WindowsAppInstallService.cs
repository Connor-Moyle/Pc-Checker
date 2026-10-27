using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PcChecker.Core.Common;

namespace PcChecker.Core.Updates;

/// <summary>
/// Copies the running PcChecker.exe to <c>C:\Program Files\PC Checker</c>. The folder is created by this elevated
/// process, so it inherits Program Files' permissions (only administrators can change it), which is what Replace Task
/// Manager, Start with Windows and the schedules check for. Adds an all-users Start menu shortcut and starts the new
/// copy with <c>--wait-pid</c>. Settings live in the user's AppData, so they are kept.
/// </summary>
public sealed class WindowsAppInstallService : IAppInstallService
{
    private readonly ILogger<WindowsAppInstallService> _logger;

    public WindowsAppInstallService(ILogger<WindowsAppInstallService> logger)
    {
        _logger = logger;
    }

    public string? InstallPath => AppInstallRules.InstallPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    public bool CanInstall => OperatingSystem.IsWindows() && InstallPath is not null
        && AppInstallRules.CanInstall(Environment.ProcessPath, ProgramFilesRoots());

    public OperationResult InstallAndStart()
    {
        var current = Environment.ProcessPath;
        var target = InstallPath;
        if (!CanInstall || current is null || target is null)
        {
            return OperationResult.Fail("This copy of PC Checker is already in Program Files or cannot install itself.");
        }

        var folder = Path.GetDirectoryName(target)!;
        var temp = target + ".new";
        var old = ExeUpdateRules.OldFilePath(target);
        try
        {
            Directory.CreateDirectory(folder);
            TryDelete(temp);

            // Written inside the new folder, so the file inherits its permissions.
            using (var source = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(copy);
            }

            if (File.Exists(target))
            {
                // An older installed copy: a file Windows may still have open can be renamed, not overwritten.
                TryDelete(old);
                File.Move(target, old);
            }

            File.Move(temp, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            TryDelete(temp);
            _logger.LogError(ex, "Copying PC Checker to {Target} failed.", target);
            return OperationResult.Fail($"PC Checker could not be copied to {folder} ({ex.Message}). Nothing was changed.", ex);
        }

        TryCreateShortcut(target, folder);

        try
        {
            var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = folder };
            start.ArgumentList.Add("--wait-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogError(ex, "The installed copy {Target} could not be started.", target);
            return OperationResult.Fail($"PC Checker was copied to {folder} but could not be started ({ex.Message}). Close this window and open PC Checker from the Start menu.", ex);
        }

        _logger.LogInformation("Installed PC Checker from {Source} to {Target}; switching to the installed copy.", current, target);
        return OperationResult.Ok($"PC Checker is installed in {folder} and opens from there now. The copy you started from is not needed any more.");
    }

    private static string[] ProgramFilesRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];

    /// <summary>All-users Start menu shortcut through the Windows Script Host shell object (late bound). Optional.</summary>
    private void TryCreateShortcut(string target, string folder)
    {
        try
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            var type = string.IsNullOrEmpty(programs) ? null : Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(Path.Combine(programs, AppInstallRules.ShortcutName));
            link.TargetPath = target;
            link.WorkingDirectory = folder;
            link.Description = "PC Checker";
            link.Save();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The Start menu shortcut for {Target} could not be created.", target);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

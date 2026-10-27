using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcChecker.Core.Common;
using PcChecker.Core.Logging;
using PcChecker.Core.Restore;

namespace PcChecker.Core.Services;

/// <summary>
/// Reads every Windows service through WMI (<c>Win32_Service</c>: structured and locale-independent, with the
/// description, log-on account, PID and delayed-start flag) and performs actions through the Service Control Manager
/// (<see cref="ServiceControlNative"/>). The load-order group and dependency list come from the service's registry
/// key. Every change goes through the change guard and is journaled with the previous value where one applies.
/// </summary>
public sealed partial class WindowsServiceManagementService : IServiceManagementService
{
    private const string Module = "Services";
    private const string ServicesKeyRoot = @"SYSTEM\CurrentControlSet\Services";

    private readonly IChangeGuard _guard;
    private readonly ILogger<WindowsServiceManagementService> _logger;

    public WindowsServiceManagementService(IChangeGuard guard, ILogger<WindowsServiceManagementService> logger)
    {
        _guard = guard;
        _logger = logger;
    }

    public Task<IReadOnlyList<ServiceDetails>> GetServicesAsync(CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult<IReadOnlyList<ServiceDetails>>([]);
        }

        return Task.Run<IReadOnlyList<ServiceDetails>>(() =>
        {
            var list = new List<ServiceDetails>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DisplayName, Description, State, StartMode, DelayedAutoStart, StartName, PathName, ProcessId, AcceptStop, AcceptPause FROM Win32_Service");
                foreach (ManagementObject o in searcher.Get())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        list.Add(Map(o));
                    }
                    catch (ManagementException ex)
                    {
                        _logger.LogDebug(ex, "A service row could not be read; skipping it.");
                    }
                    finally
                    {
                        o.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The service list could not be read from WMI.");
            }

            return list.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }, cancellationToken);
    }

    public Task<ServiceDependencies> GetDependenciesAsync(string serviceName, CancellationToken cancellationToken)
    {
        if (!PlatformGuard.IsWindows || string.IsNullOrWhiteSpace(serviceName))
        {
            return Task.FromResult(ServiceDependencies.Empty(serviceName));
        }

        return Task.Run(() =>
        {
            var dependsOn = ReadDependsOn(serviceName);
            IReadOnlyList<string> requiredBy;
            try
            {
                requiredBy = ServiceControlNative.EnumerateDependents(serviceName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dependent services of {Service} could not be read.", serviceName);
                requiredBy = [];
            }

            return new ServiceDependencies(serviceName, dependsOn, requiredBy);
        }, cancellationToken);
    }

    public Task<OperationResult> StartAsync(ServiceDetails service, CancellationToken cancellationToken) =>
        TransientAsync(service, ServiceControlNative.ControlAction.Start, $"Start '{service.DisplayName}'",
            $"{service.DisplayName} is now running.", cancellationToken);

    public Task<OperationResult> StopAsync(ServiceDetails service, CancellationToken cancellationToken)
    {
        var (allowed, reason) = ServiceSafetyRules.CanStop(service);
        if (!allowed)
        {
            return Task.FromResult(OperationResult.Fail(reason ?? "This service cannot be stopped."));
        }

        return TransientAsync(service, ServiceControlNative.ControlAction.Stop, $"Stop '{service.DisplayName}'",
            $"{service.DisplayName} is now stopped.", cancellationToken);
    }

    public Task<OperationResult> PauseAsync(ServiceDetails service, CancellationToken cancellationToken) =>
        TransientAsync(service, ServiceControlNative.ControlAction.Pause, $"Pause '{service.DisplayName}'",
            $"{service.DisplayName} is paused.", cancellationToken);

    public Task<OperationResult> ContinueAsync(ServiceDetails service, CancellationToken cancellationToken) =>
        TransientAsync(service, ServiceControlNative.ControlAction.Continue, $"Continue '{service.DisplayName}'",
            $"{service.DisplayName} has resumed.", cancellationToken);

    public Task<OperationResult> RestartAsync(ServiceDetails service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        var (allowed, reason) = ServiceSafetyRules.CanStop(service);
        if (!allowed)
        {
            return Task.FromResult(OperationResult.Fail(reason ?? "This service cannot be restarted."));
        }

        var options = new ChangeOptions(false, false, Module, service.Name, Reversible: false, UndoHint: null);
        return _guard.RunGuardedAsync($"Restart '{service.DisplayName}'", options, async ct =>
        {
            // Stop the dependents first (deepest first), then the service; start again in reverse.
            var requiredBy = ServiceDependencyOrder.BuildRequiredBy(
                new Dictionary<string, IReadOnlyList<string>> { [service.Name] = [] });
            var dependents = ServiceControlNative.EnumerateDependents(service.Name);
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [service.Name] = dependents,
            };
            var stopOrder = ServiceDependencyOrder.StopOrder(service.Name, ServiceDependencyOrder.BuildRequiredBy(map));

            foreach (var name in stopOrder)
            {
                ct.ThrowIfCancellationRequested();
                var stopError = ServiceControlNative.Control(name, ServiceControlNative.ControlAction.Stop);
                if (stopError is not null && name.Equals(service.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail(stopError);
                }
            }

            await Task.Delay(400, ct).ConfigureAwait(false);

            foreach (var name in ServiceDependencyOrder.StartOrder(stopOrder))
            {
                ct.ThrowIfCancellationRequested();
                var startError = ServiceControlNative.Control(name, ServiceControlNative.ControlAction.Start);
                if (startError is not null && name.Equals(service.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail(startError);
                }
            }

            return OperationResult.Ok($"{service.DisplayName} was restarted.");
        }, null, cancellationToken);
    }

    public Task<OperationResult> SetStartupTypeAsync(ServiceDetails service, ServiceStartupType startupType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Changing a service start type is only possible on Windows."));
        }

        var (allowed, reason) = ServiceSafetyRules.CanSetStartup(service.Name, startupType);
        if (!allowed)
        {
            return Task.FromResult(OperationResult.Fail(reason ?? "This start type cannot be set."));
        }

        var mapped = ServiceStartupMapping.ToWin32(startupType);
        if (mapped is not { } win32)
        {
            return Task.FromResult(OperationResult.Fail($"{ServiceStartupMapping.Label(startupType)} cannot be set from here."));
        }

        var previous = service.StartupType;
        var options = new ChangeOptions(null, null, Module, service.Name, Reversible: true,
            UndoHint: $"Set the start type of '{service.DisplayName}' back to {ServiceStartupMapping.Label(previous)} on the Services page.")
        {
            // Stored on the journal record so the Action Journal can offer "undo" back to the old type.
        };

        return _guard.RunGuardedAsync($"Set '{service.DisplayName}' start type to {ServiceStartupMapping.Label(startupType)}", options, ct =>
        {
            var error = ServiceControlNative.SetStartType(service.Name, win32.StartType, win32.Delayed);
            if (error is not null)
            {
                return Task.FromResult(OperationResult.Fail(error));
            }

            string note = reason is null ? string.Empty : " " + reason;
            return Task.FromResult(OperationResult.Ok($"{service.DisplayName} is now set to {ServiceStartupMapping.Label(startupType)}.{note}"));
        }, null, cancellationToken);
    }

    public OperationResult OpenServicesConsole() => ShellLauncher.Open("services.msc");

    public OperationResult OpenFileLocation(ServiceDetails service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var path = ServiceBinaryPath.ExtractExecutable(service.BinaryPath);
        if (path is null || !System.IO.File.Exists(path))
        {
            return OperationResult.Fail($"The program file of {service.DisplayName} is not known or no longer exists.");
        }

        return ShellLauncher.Open("explorer.exe", $"/select,\"{path}\"");
    }

    public OperationResult SearchOnline(ServiceDetails service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return ShellLauncher.Open(ServiceBinaryPath.SearchUrl(service.Name, service.DisplayName));
    }

    private Task<OperationResult> TransientAsync(ServiceDetails service, ServiceControlNative.ControlAction action,
        string description, string success, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!PlatformGuard.IsWindows)
        {
            return Task.FromResult(OperationResult.Fail("Service actions are only possible on Windows."));
        }

        // Starting or stopping a service changes no persistent setting, so no restore point; it is still journaled.
        var options = new ChangeOptions(false, false, Module, service.Name, Reversible: false, UndoHint: null);
        return _guard.RunGuardedAsync(description, options, ct =>
        {
            var error = ServiceControlNative.Control(service.Name, action);
            return Task.FromResult(error is null ? OperationResult.Ok(success) : OperationResult.Fail(error));
        }, null, cancellationToken);
    }

    private static ServiceDetails Map(ManagementObject o)
    {
        string name = o["Name"]?.ToString() ?? string.Empty;
        string display = o["DisplayName"]?.ToString() ?? name;
        string? description = o["Description"]?.ToString();
        bool delayed = o["DelayedAutoStart"] is bool d && d;
        var startup = ServiceStartupMapping.Parse(o["StartMode"]?.ToString()) ?? ServiceStartupType.Unknown;
        if (startup == ServiceStartupType.Automatic && delayed)
        {
            startup = ServiceStartupType.AutomaticDelayed;
        }

        var state = ParseState(o["State"]?.ToString());
        string? path = o["PathName"]?.ToString();
        bool microsoft = ServiceBinaryPath.IsMicrosoft(path);
        int? pid = o["ProcessId"] is uint p && p > 0 ? (int)p : null;
        bool canStop = o["AcceptStop"] is bool cs && cs;
        bool canPause = o["AcceptPause"] is bool cp && cp;

        return new ServiceDetails(name, display, description, state, startup, o["StartName"]?.ToString(), pid,
            ReadGroup(name), path, microsoft, canStop, canPause);
    }

    private static ServiceRunState ParseState(string? state) => (state ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "running" => ServiceRunState.Running,
        "stopped" => ServiceRunState.Stopped,
        "paused" => ServiceRunState.Paused,
        "start pending" => ServiceRunState.StartPending,
        "stop pending" => ServiceRunState.StopPending,
        "continue pending" => ServiceRunState.ContinuePending,
        "pause pending" => ServiceRunState.PausePending,
        _ => ServiceRunState.Unknown,
    };

    private static string? ReadGroup(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKeyRoot}\{name}");
            return key?.GetValue("Group") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadDependsOn(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKeyRoot}\{name}");
            return key?.GetValue("DependOnService") is string[] values
                ? values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return [];
        }
    }
}

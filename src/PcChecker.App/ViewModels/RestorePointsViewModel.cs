using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Restore;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Reusable restore-point panel: lists existing points, creates one on request, turns System Protection on when it is
/// off and opens the Windows System Restore tool. Embedded by <c>RestorePointsControl</c> on Settings, Dashboard and
/// the Windows Features page.
/// </summary>
public sealed partial class RestorePointsViewModel : ViewModelBase
{
    public const string DefaultDescription = "Manual restore point (PC Checker)";

    private readonly IRestorePointService _restorePoints;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<RestorePointsViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProtectionOff))]
    [NotifyPropertyChangedFor(nameof(ProtectionText))]
    private bool _isProtectionEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProtectionOff))]
    [NotifyPropertyChangedFor(nameof(ProtectionText))]
    private bool _isProtectionKnown;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateRestorePointCommand))]
    private string _newDescription = DefaultDescription;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPoints))]
    [NotifyPropertyChangedFor(nameof(LatestText))]
    private RestorePoint? _latest;

    [ObservableProperty]
    private DateTimeOffset? _lastRefreshed;

    public RestorePointsViewModel(ISettingsService settings, IRestorePointService restorePoints, IDialogService dialogs,
        INotificationService notifications, ILogger<RestorePointsViewModel> logger)
        : base(settings)
    {
        _restorePoints = restorePoints;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
    }

    public ObservableCollection<RestorePoint> Points { get; } = [];

    public string SystemDrive { get; } = PlatformGuard.IsWindows
        ? (System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\")
        : "C:\\";

    public bool HasPoints => Points.Count > 0;

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>True only when we know protection is off (not while unknown).</summary>
    public bool IsProtectionOff => IsProtectionKnown && !IsProtectionEnabled;

    public string ProtectionText => !IsProtectionKnown
        ? "Checking whether System Protection is on…"
        : IsProtectionEnabled
            ? $"System Protection is on for {SystemDrive}. Windows keeps restore points on this drive."
            : $"System Protection is off for {SystemDrive}. No restore points can be created until it is turned on.";

    public string LatestText => Latest is null
        ? "No restore points yet."
        : $"Latest: {Latest.Description} — {Latest.CreationTime.LocalDateTime:g}";

    public override Task InitializeAsync(CancellationToken cancellationToken) => RunBusyAsync(LoadAsync, "Reading restore points…");

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(LoadAsync, "Reading restore points…");

    [RelayCommand]
    private Task RefreshPointsAsync() => RunBusyAsync(LoadAsync, "Reading restore points…");

    private bool CanCreate() => !string.IsNullOrWhiteSpace(NewDescription);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateRestorePointAsync() => RunBusyAsync(async ct =>
    {
        StatusMessage = null;
        var description = NewDescription.Trim();
        var result = await _restorePoints.CreateAsync(description, ct);
        if (result.Success && result.Value is not null)
        {
            StatusMessage = result.Message;
            _notifications.Success("Restore point created", result.Value.Description);
            NewDescription = DefaultDescription;
        }
        else
        {
            ErrorMessage = result.Message.EndsWith('.') ? result.Message : result.Message + ".";
            _logger.LogWarning("Restore point creation failed: {Message}", result.Message);
        }

        await LoadAsync(ct);
    }, "Creating a restore point… this can take a minute");

    [RelayCommand]
    private async Task EnableProtectionAsync()
    {
        bool ok = await _dialogs.ConfirmAsync("Turn on System Protection?",
            $"Windows will start keeping restore points for {SystemDrive}. They use a few percent of the drive and let you undo driver, feature and settings changes.",
            "Turn on");
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            StatusMessage = null;
            var result = await _restorePoints.EnableAsync(SystemDrive, ct);
            if (result.Success)
            {
                StatusMessage = result.Message;
                _notifications.Success("System Protection", result.Message);
            }
            else
            {
                ErrorMessage = result.Message;
            }

            await LoadAsync(ct);
        }, "Turning on System Protection…");
    }

    [RelayCommand]
    private void OpenSystemRestore() => Open("rstrui.exe");

    [RelayCommand]
    private void OpenSystemProtectionSettings() => Open("SystemPropertiesProtection.exe");

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Restore points panel error.");

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsProtectionEnabled = await _restorePoints.IsEnabledAsync(cancellationToken);
            IsProtectionKnown = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the System Protection state.");
            IsProtectionKnown = false;
        }

        var points = await _restorePoints.ListAsync(cancellationToken);
        Points.Clear();
        foreach (var point in points.OrderByDescending(p => p.SequenceNumber))
        {
            Points.Add(point);
        }

        Latest = Points.FirstOrDefault();
        LastRefreshed = DateTimeOffset.Now;
        OnPropertyChanged(nameof(HasPoints));
    }

    private void Open(string target)
    {
        var result = ShellLauncher.Open(target);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }
}

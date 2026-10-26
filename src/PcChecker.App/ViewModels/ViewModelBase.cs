using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Base for every view model: busy state with cancellation, an error message slot, and <see cref="IsAdvancedMode"/>
/// kept in sync with settings.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    private readonly ISettingsService? _settings;
    private CancellationTokenSource? _busyCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isAdvancedMode;

    protected ViewModelBase()
    {
    }

    protected ViewModelBase(ISettingsService settings)
    {
        _settings = settings;
        _isAdvancedMode = settings.Current.General.AdvancedMode;
        settings.Changed += OnSettingsChanged;
    }

    public bool IsNotBusy => !IsBusy;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    protected ISettingsService Settings => _settings ?? throw new InvalidOperationException("This view model was created without settings.");

    /// <summary>Runs a long operation with the busy overlay shown; exceptions become <see cref="ErrorMessage"/>.</summary>
    protected async Task RunBusyAsync(Func<CancellationToken, Task> work, string busyText = "Working…")
    {
        if (IsBusy)
        {
            return;
        }

        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        var token = _busyCts.Token;
        ErrorMessage = null;
        BusyText = busyText;
        IsBusy = true;
        try
        {
            await work(token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user; nothing to report.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            OnBusyError(ex);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            _busyCts.Dispose();
            _busyCts = null;
        }
    }

    /// <summary>Hook for derived classes to log errors.</summary>
    protected virtual void OnBusyError(Exception exception)
    {
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        try
        {
            _busyCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [RelayCommand]
    private void ClearError() => ErrorMessage = null;

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        Services.DispatcherHelper.PostToUi(() => IsAdvancedMode = settings.General.AdvancedMode);
}

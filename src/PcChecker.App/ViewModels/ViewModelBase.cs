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
    private readonly object _initGate = new();
    private CancellationTokenSource? _busyCts;
    private Task? _initializeTask;
    private int _busyCancellations;
    private bool _isInitialized;
    private bool _initializationCancelled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    /// <summary>
    /// False while the current busy operation cannot be stopped part-way (driver installs, uninstalls, Windows feature
    /// changes); the overlay then hides its Cancel button and <see cref="CancelCommand"/> is disabled.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _canCancelBusy = true;

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

    /// <summary>
    /// True once <see cref="InitializeAsync"/> has completed (successfully or with an error shown). Stays false when the
    /// user cancelled the first load, so the next <see cref="EnsureInitializedAsync"/> runs it again.
    /// </summary>
    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
    }

    /// <summary>True when the last initialization attempt was cancelled; the page loads again when shown next.</summary>
    public bool InitializationCancelled
    {
        get => _initializationCancelled;
        private set => SetProperty(ref _initializationCancelled, value);
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    protected ISettingsService Settings => _settings ?? throw new InvalidOperationException("This view model was created without settings.");

    /// <summary>
    /// Runs a long operation with the busy overlay shown; exceptions become <see cref="ErrorMessage"/>. Returns false
    /// when the operation was skipped (already busy) or cancelled. A cancellation is also recorded so that
    /// <see cref="EnsureInitializedAsync"/> can tell a cancelled first load from a finished one. Pass
    /// <paramref name="cancellable"/> false for operations that cannot be stopped part-way: Cancel is hidden and the work
    /// gets a token that never fires, so its result and any restart notice are always reported.
    /// </summary>
    protected async Task<bool> RunBusyAsync(Func<CancellationToken, Task> work, string busyText = "Working…", bool cancellable = true)
    {
        if (IsBusy)
        {
            return false;
        }

        _busyCts?.Dispose();
        _busyCts = new CancellationTokenSource();
        var token = cancellable ? _busyCts.Token : CancellationToken.None;
        ErrorMessage = null;
        BusyText = busyText;
        CanCancelBusy = cancellable;
        IsBusy = true;
        try
        {
            await work(token);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user; nothing to report, but let a pending initialization know it did not finish.
            Interlocked.Increment(ref _busyCancellations);
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            OnBusyError(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            CanCancelBusy = true;
            _busyCts.Dispose();
            _busyCts = null;
        }
    }

    /// <summary>Hook for derived classes to log errors.</summary>
    protected virtual void OnBusyError(Exception exception)
    {
    }

    /// <summary>
    /// Called once, the first time the page is shown. Load the last results or run the default scan here.
    /// The default does nothing.
    /// </summary>
    public virtual Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Re-runs the page's main query on request (header "Refresh"). The default does nothing.</summary>
    public virtual Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs <see cref="InitializeAsync"/> exactly once, even when called from several places (page Loaded, navigation,
    /// screenshot runner). Exceptions become <see cref="ErrorMessage"/>; a cancelled initialization may run again later.
    /// </summary>
    public Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        lock (_initGate)
        {
            if (_initializeTask is not null)
            {
                return _initializeTask;
            }

            var task = InitializeCoreAsync(cancellationToken);

            // A run cancelled synchronously has already cleared the slot; keep it clear so the next call retries.
            if (!(task.IsCompleted && InitializationCancelled))
            {
                _initializeTask = task;
            }

            return task;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        // Most pages load through RunBusyAsync, which swallows the user's Cancel; compare its cancellation count so a
        // cancelled first load is not mistaken for a finished one.
        var cancellationsBefore = Volatile.Read(ref _busyCancellations);
        InitializationCancelled = false;
        try
        {
            await InitializeAsync(cancellationToken);
            if (Volatile.Read(ref _busyCancellations) != cancellationsBefore)
            {
                MarkInitializationCancelled();
                return;
            }

            IsInitialized = true;
        }
        catch (OperationCanceledException)
        {
            MarkInitializationCancelled();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            OnBusyError(ex);
            IsInitialized = true;
        }
    }

    private void MarkInitializationCancelled()
    {
        lock (_initGate)
        {
            _initializeTask = null;
        }

        InitializationCancelled = true;
    }

    private bool CanCancel() => IsBusy && CanCancelBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!CanCancelBusy)
        {
            return;
        }

        try
        {
            _busyCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        Services.DispatcherHelper.PostToUi(() => IsAdvancedMode = settings.General.AdvancedMode);

    /// <summary>
    /// Runs <paramref name="reload"/> on the UI thread when the settings section picked by <paramref name="section"/> is
    /// replaced as a whole (a setup import, its undo, or a reset), so a page that has already loaded never shows or edits
    /// the old copy. Ordinary saves keep the same object and do not trigger it.
    /// </summary>
    protected void ReloadWhenReplaced<T>(Func<AppSettings, T?> section, Action reload)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(reload);
        var settings = Settings;
        var last = section(settings.Current);
        settings.Changed += (_, current) =>
        {
            var now = section(current);
            if (ReferenceEquals(now, last))
            {
                return;
            }

            last = now;
            Services.DispatcherHelper.PostToUi(reload);
        };
    }
}

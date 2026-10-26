using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Input;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Keyboard &amp; Mouse page: pointer speed and acceleration, double-click, wheel, keyboard repeat, accessibility
/// toggles, the polling rate meter, profiles, detected devices and vendor software links. Every Apply confirms first.
/// </summary>
public sealed partial class InputViewModel : ViewModelBase
{
    private static readonly HashSet<string> MouseProperties =
    [
        nameof(MouseSpeed), nameof(EnhancePointerPrecision), nameof(DoubleClickMs), nameof(SwapButtons), nameof(ScrollLines),
        nameof(ScrollOneScreen), nameof(PointerTrails), nameof(HidePointerWhileTyping), nameof(SnapToDefaultButton),
    ];

    private static readonly HashSet<string> KeyboardProperties =
    [
        nameof(RepeatDelay), nameof(RepeatRate), nameof(CursorBlinkMs), nameof(StickyKeys), nameof(FilterKeys), nameof(ToggleKeys),
    ];

    private readonly IInputService _input;
    private readonly IMousePollingMeter _pollingMeter;
    private readonly IHardwareInventoryService _hardware;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<InputViewModel> _logger;

    private MouseSettings _loadedMouse = new();
    private KeyboardSettings _loadedKeyboard = new();
    private bool _loading;

    // Mouse
    [ObservableProperty] private int _mouseSpeed = InputMath.DefaultSpeed;
    [ObservableProperty] private bool _enhancePointerPrecision = true;
    [ObservableProperty] private int _doubleClickMs = 500;
    [ObservableProperty] private bool _swapButtons;
    [ObservableProperty] private int _scrollLines = 3;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScrollLinesEnabled))]
    private bool _scrollOneScreen;
    [ObservableProperty] private int _pointerTrails;
    [ObservableProperty] private bool _hidePointerWhileTyping = true;
    [ObservableProperty] private bool _snapToDefaultButton;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyMouseCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertMouseCommand))]
    private bool _hasMouseChanges;

    [ObservableProperty] private string _speedText = "6/11";
    [ObservableProperty] private string _speedNote = string.Empty;
    [ObservableProperty] private string _doubleClickText = string.Empty;
    [ObservableProperty] private string _scrollText = string.Empty;
    [ObservableProperty] private string _trailsText = string.Empty;

    // Keyboard
    [ObservableProperty] private int _repeatDelay = 1;
    [ObservableProperty] private int _repeatRate = 31;
    [ObservableProperty] private int _cursorBlinkMs = 530;
    [ObservableProperty] private bool _stickyKeys;
    [ObservableProperty] private bool _filterKeys;
    [ObservableProperty] private bool _toggleKeys;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyKeyboardCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertKeyboardCommand))]
    private bool _hasKeyboardChanges;

    [ObservableProperty] private string _repeatDelayText = string.Empty;
    [ObservableProperty] private string _repeatRateText = string.Empty;
    [ObservableProperty] private string _cursorBlinkText = string.Empty;
    [ObservableProperty] private string _keyboardTestText = string.Empty;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private DateTimeOffset? _lastLoaded;

    public InputViewModel(
        ISettingsService settings,
        IInputService input,
        IMousePollingMeter pollingMeter,
        IHardwareInventoryService hardware,
        IDialogService dialogs,
        INotificationService notifications,
        INavigationService navigation,
        ILogger<InputViewModel> logger)
        : base(settings)
    {
        _input = input;
        _pollingMeter = pollingMeter;
        _hardware = hardware;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        PollingTable = PollingRateMath.Table;
        RefreshDerivedText();
        RebuildProfiles();
    }

    public ObservableCollection<InputProfileItem> Profiles { get; } = [];
    public ObservableCollection<InputDeviceItem> Devices { get; } = [];
    public ObservableCollection<Core.Drivers.VendorLink> VendorLinks { get; } = [];
    public IReadOnlyList<PollingRateInfo> PollingTable { get; }

    public bool HasDevices => Devices.Count > 0;
    public bool ScrollLinesEnabled => !ScrollOneScreen;
    public bool HasVendorLinks => VendorLinks.Count > 0;

    public override Task InitializeAsync(CancellationToken cancellationToken) => RunBusyAsync(async ct =>
    {
        await LoadSettingsAsync(ct);
        await LoadDevicesAsync(scanIfMissing: true, ct);
    }, "Reading keyboard and mouse settings…");

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(async ct =>
    {
        await LoadSettingsAsync(ct);
        await LoadDevicesAsync(scanIfMissing: false, ct);
    }, "Reading keyboard and mouse settings…");

    [RelayCommand]
    private Task RefreshNowAsync() => RefreshAsync(CancellationToken.None);

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Keyboard & Mouse page error.");

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null)
        {
            return;
        }

        if (MouseProperties.Contains(e.PropertyName))
        {
            RefreshDerivedText();
            HasMouseChanges = !InputMath.SameAs(BuildMouse(), _loadedMouse);
        }
        else if (KeyboardProperties.Contains(e.PropertyName))
        {
            RefreshDerivedText();
            HasKeyboardChanges = !InputMath.SameAs(BuildKeyboard(), _loadedKeyboard);
        }
    }

    // ----- Loading -----

    private async Task LoadSettingsAsync(CancellationToken ct)
    {
        var mouse = await _input.GetMouseAsync(ct);
        var keyboard = await _input.GetKeyboardAsync(ct);
        ShowSettings(mouse, keyboard);
        LastLoaded = DateTimeOffset.Now;
    }

    private void ShowSettings(MouseSettings mouse, KeyboardSettings keyboard)
    {
        _loadedMouse = mouse.Clone();
        _loadedKeyboard = keyboard.Clone();
        ShowMouse(mouse);
        ShowKeyboard(keyboard);
        MarkActiveProfile();
    }

    /// <summary>Puts the values into the editors without touching the loaded snapshot, then recomputes the dirty flag.</summary>
    private void ShowMouse(MouseSettings mouse)
    {
        _loading = true;
        try
        {
            MouseSpeed = mouse.Speed;
            EnhancePointerPrecision = mouse.EnhancePointerPrecision;
            DoubleClickMs = mouse.DoubleClickMs;
            SwapButtons = mouse.SwapButtons;
            ScrollLines = mouse.ScrollLines;
            ScrollOneScreen = mouse.ScrollOneScreen;
            PointerTrails = mouse.PointerTrails;
            HidePointerWhileTyping = mouse.HidePointerWhileTyping;
            SnapToDefaultButton = mouse.SnapToDefaultButton;
        }
        finally
        {
            _loading = false;
        }

        RefreshDerivedText();
        HasMouseChanges = !InputMath.SameAs(BuildMouse(), _loadedMouse);
    }

    private void ShowKeyboard(KeyboardSettings keyboard)
    {
        _loading = true;
        try
        {
            RepeatDelay = keyboard.RepeatDelay;
            RepeatRate = keyboard.RepeatRate;
            CursorBlinkMs = keyboard.CursorBlinkMs;
            StickyKeys = keyboard.StickyKeys;
            FilterKeys = keyboard.FilterKeys;
            ToggleKeys = keyboard.ToggleKeys;
        }
        finally
        {
            _loading = false;
        }

        RefreshDerivedText();
        HasKeyboardChanges = !InputMath.SameAs(BuildKeyboard(), _loadedKeyboard);
    }

    private MouseSettings BuildMouse() => new()
    {
        Speed = MouseSpeed,
        EnhancePointerPrecision = EnhancePointerPrecision,
        DoubleClickMs = DoubleClickMs,
        SwapButtons = SwapButtons,
        ScrollLines = ScrollLines,
        ScrollOneScreen = ScrollOneScreen,
        PointerTrails = PointerTrails,
        HidePointerWhileTyping = HidePointerWhileTyping,
        SnapToDefaultButton = SnapToDefaultButton,
    };

    private KeyboardSettings BuildKeyboard() => new()
    {
        RepeatDelay = RepeatDelay,
        RepeatRate = RepeatRate,
        CursorBlinkMs = CursorBlinkMs,
        StickyKeys = StickyKeys,
        FilterKeys = FilterKeys,
        ToggleKeys = ToggleKeys,
    };

    private void RefreshDerivedText()
    {
        SpeedText = InputMath.SpeedToSliderText(MouseSpeed);
        SpeedNote = InputMath.SpeedNote(MouseSpeed);
        DoubleClickText = InputMath.DoubleClickText(DoubleClickMs);
        ScrollText = InputMath.ScrollText(ScrollLines, ScrollOneScreen);
        TrailsText = InputMath.TrailsText(PointerTrails);
        RepeatDelayText = InputMath.RepeatDelayText(RepeatDelay);
        RepeatRateText = InputMath.RepeatRateText(RepeatRate);
        CursorBlinkText = InputMath.CursorBlinkText(CursorBlinkMs);
    }

    // ----- Mouse -----

    [RelayCommand(CanExecute = nameof(HasMouseChanges))]
    private async Task ApplyMouseAsync()
    {
        var target = InputMath.Normalize(BuildMouse());
        var changes = InputMath.Describe(_loadedMouse, target);
        if (changes.Count == 0)
        {
            HasMouseChanges = false;
            return;
        }

        bool ok = await _dialogs.ConfirmAsync("Apply mouse settings?",
            "These changes will be applied to Windows right away:\n\n• " + string.Join("\n• ", changes)
            + "\n\nYou can undo them at any time by applying the \"Windows default\" profile.", "Apply");
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _input.ApplyMouseAsync(target, ct);
            Report(result, "Mouse settings");
            await LoadSettingsAsync(ct);
        }, "Applying mouse settings…");
    }

    [RelayCommand(CanExecute = nameof(HasMouseChanges))]
    private void RevertMouse() => ShowMouse(_loadedMouse);

    /// <summary>Loads the Windows defaults into the editors (nothing is applied until Apply is pressed).</summary>
    [RelayCommand]
    private void ResetMouseToDefaults() => ShowMouse(new MouseSettings());

    // ----- Keyboard -----

    [RelayCommand(CanExecute = nameof(HasKeyboardChanges))]
    private async Task ApplyKeyboardAsync()
    {
        var target = InputMath.Normalize(BuildKeyboard());
        var changes = InputMath.Describe(_loadedKeyboard, target);
        if (changes.Count == 0)
        {
            HasKeyboardChanges = false;
            return;
        }

        bool ok = await _dialogs.ConfirmAsync("Apply keyboard settings?",
            "These changes will be applied to Windows right away:\n\n• " + string.Join("\n• ", changes)
            + "\n\nYou can undo them at any time by applying the \"Windows default\" profile.", "Apply");
        if (!ok)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var result = await _input.ApplyKeyboardAsync(target, ct);
            Report(result, "Keyboard settings");
            await LoadSettingsAsync(ct);
        }, "Applying keyboard settings…");
    }

    [RelayCommand(CanExecute = nameof(HasKeyboardChanges))]
    private void RevertKeyboard() => ShowKeyboard(_loadedKeyboard);

    /// <summary>Loads the Windows defaults into the editors (nothing is applied until Apply is pressed).</summary>
    [RelayCommand]
    private void ResetKeyboardToDefaults() => ShowKeyboard(new KeyboardSettings());

    [RelayCommand]
    private void ClearKeyboardTest() => KeyboardTestText = string.Empty;

    private void Report(OperationResult result, string what)
    {
        StatusMessage = result.Message;
        if (result.Success)
        {
            _notifications.Success(what, result.Message);
        }
        else
        {
            ErrorMessage = result.Message;
            _notifications.Error(what, result.Message);
        }
    }
}

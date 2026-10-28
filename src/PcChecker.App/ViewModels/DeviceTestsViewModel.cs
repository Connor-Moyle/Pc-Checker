using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Audio;
using PcChecker.Core.Common;
using PcChecker.Core.DeviceTests;
using PcChecker.Core.Displays;
using PcChecker.Core.Hardware;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Device tests page: one card per test (controller, keyboard, mouse, screen, speakers, microphone). The controller,
/// speaker and microphone tests run on the page; the keyboard, mouse and screen tests open their own window so they
/// can capture keys and fill the screen. Everything is read-only: nothing on the PC is changed. Live work (controller
/// polling, vibration, sounds, the microphone) stops when the page is left.
/// </summary>
public sealed partial class DeviceTestsViewModel : ViewModelBase
{
    private const string MicrophonePrivacyUri = "ms-settings:privacy-microphone";
    private const string SoundSettingsUri = "ms-settings:sound";

    private readonly IGamepadTestService _gamepads;
    private readonly ISoundTestService _sound;
    private readonly IAudioService _audio;
    private readonly IDisplayService _displays;
    private readonly IHardwareInventoryService _hardware;
    private readonly DeviceTestWindows _windows;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<DeviceTestsViewModel> _logger;
    private bool _pageVisible;
    private bool _demoAutoStartPending;

    [ObservableProperty] private string _subtitle = Loc.T("Check that your controller, keyboard, mouse, screen, speakers and microphone work.");
    [ObservableProperty] private string _controllerSummary = Loc.T("Looking for controllers…");
    [ObservableProperty] private string _keyboardName = Loc.T("Any keyboard");
    [ObservableProperty] private string _mouseName = Loc.T("Any mouse");
    [ObservableProperty] private string _screenSummary = string.Empty;
    [ObservableProperty] private string _outputName = Loc.T("No output found");
    [ObservableProperty] private string _inputName = Loc.T("No microphone found");
    [ObservableProperty] private int _controllerCount;
    [ObservableProperty] private int _screenCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScreenTestCommand))]
    private DisplayChoice? _selectedDisplay;

    public DeviceTestsViewModel(
        IGamepadTestService gamepads,
        ISoundTestService sound,
        IAudioService audio,
        IDisplayService displays,
        IHardwareInventoryService hardware,
        DeviceTestWindows windows,
        IDialogService dialogs,
        INotificationService notifications,
        INavigationService navigation,
        ISettingsService settings,
        ILogger<DeviceTestsViewModel> logger)
        : base(settings)
    {
        _gamepads = gamepads;
        _sound = sound;
        _audio = audio;
        _displays = displays;
        _hardware = hardware;
        _windows = windows;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        _gamepads.GamepadsChanged += OnGamepadsChanged;
        ButtonLights = [];
    }

    public ObservableCollection<DisplayChoice> Displays { get; } = [];

    public bool HasDisplays => Displays.Count > 0;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunBusyAsync(LoadAsync, Loc.T("Looking for devices…"));
        _demoAutoStartPending = AppOptions.Current.IsDemo;
        TryDemoAutoStart();
    }

    /// <summary>
    /// Demo mode shows the tests running once the page is on screen: the controller panel open with a drift check
    /// (the demo controller starts at rest), and the microphone meter.
    /// </summary>
    private void TryDemoAutoStart()
    {
        if (!_demoAutoStartPending || !_pageVisible)
        {
            return;
        }

        _demoAutoStartPending = false;
        if (HasControllers)
        {
            OpenControllerTest();
            _ = CheckDriftAsync();
        }

        if (!IsListening)
        {
            _ = ToggleListeningAsync();
        }
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RunBusyAsync(LoadAsync, Loc.T("Looking for devices…"));

    /// <summary>Called by the page when it is shown: resumes the controller readings if that test is open.</summary>
    public void OnPageShown()
    {
        _pageVisible = true;
        UpdateControllerPolling();
        TryDemoAutoStart();
    }

    /// <summary>Called by the page when it is left: stops polling, vibration, sounds and the microphone.</summary>
    public void OnPageHidden()
    {
        _pageVisible = false;
        UpdateControllerPolling();
        StopRumble();
        StopSound();
        StopListening();
    }

    [RelayCommand]
    private Task RefreshDevices() => RefreshAsync(CancellationToken.None);

    private async Task LoadAsync(CancellationToken ct)
    {
        var gamepadsTask = SafeAsync(() => _gamepads.GetGamepadsAsync(ct), (IReadOnlyList<GamepadInfo>)[], "controllers");
        var audioTask = SafeAsync(() => _audio.GetDevicesAsync(ct), (IReadOnlyList<AudioDevice>)[], "sound devices");
        var displaysTask = SafeAsync(() => _displays.GetDisplaysAsync(ct), (IReadOnlyList<DisplayInfo>)[], "displays");
        await Task.WhenAll(gamepadsTask, audioTask, displaysTask);

        ShowControllers(gamepadsTask.Result);
        ShowAudio(audioTask.Result);
        ShowDisplays(displaysTask.Result);
        ShowInputDevices(_hardware.Last);
        if (_hardware.Last is null)
        {
            _ = DetectInputDevicesAsync();
        }
    }

    /// <summary>Reads the keyboard and mouse names in the background when no hardware scan has run yet.</summary>
    private async Task DetectInputDevicesAsync()
    {
        try
        {
            var inventory = await _hardware.ScanAsync(null, CancellationToken.None);
            ShowInputDevices(inventory);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Keyboard and mouse names could not be read.");
        }
    }

    private async Task<T> SafeAsync<T>(Func<Task<T>> read, T fallback, string what)
    {
        try
        {
            return await read();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device tests could not read the {What}.", what);
            return fallback;
        }
    }

    private void ShowAudio(IReadOnlyList<AudioDevice> devices)
    {
        var output = devices.FirstOrDefault(d => d.Flow == AudioFlow.Render && d.IsDefault);
        var input = devices.FirstOrDefault(d => d.Flow == AudioFlow.Capture && d.IsDefaultCommunications)
                    ?? devices.FirstOrDefault(d => d.Flow == AudioFlow.Capture && d.IsDefault);
        OutputName = output?.Name is { } outName ? outName : Loc.T("No output found");
        OutputVolume = output?.VolumePercent;
        InputName = input?.Name is { } inName ? inName : Loc.T("No microphone found");
    }

    private void ShowDisplays(IReadOnlyList<DisplayInfo> displays)
    {
        string? previous = SelectedDisplay?.Display.DeviceName;
        Displays.Clear();
        int number = 0;
        foreach (var display in displays.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.PositionX))
        {
            number++;
            Displays.Add(new DisplayChoice(display, number));
        }

        SelectedDisplay = Displays.FirstOrDefault(d => d.Display.DeviceName == previous) ?? Displays.FirstOrDefault();
        ScreenCount = Displays.Count;
        ScreenSummary = Displays.Count switch
        {
            0 => Loc.T("No screen details could be read; the test uses the main screen."),
            1 => Displays[0].Text,
            _ => Loc.F("{0} screens: pick the one to test", Displays.Count),
        };
        OnPropertyChanged(nameof(HasDisplays));
    }

    private void ShowInputDevices(HardwareInventory? inventory)
    {
        var devices = inventory?.InputDevices ?? [];
        var keyboard = devices.FirstOrDefault(d => d.Kind.Contains("keyboard", StringComparison.OrdinalIgnoreCase));
        var mouse = devices.FirstOrDefault(d => d.Kind.Contains("mouse", StringComparison.OrdinalIgnoreCase) || d.Kind.Contains("pointing", StringComparison.OrdinalIgnoreCase));
        KeyboardName = keyboard?.Name ?? Loc.T("Any keyboard");
        MouseName = mouse?.Name ?? Loc.T("Any mouse");
    }

    // ----- Keyboard, mouse and screen windows -----

    [RelayCommand]
    private void OpenKeyboardTest() => Launch(_windows.OpenKeyboard);

    [RelayCommand]
    private void OpenMouseTest() => Launch(_windows.OpenMouse);

    private bool CanStartScreenTest() => SelectedDisplay is not null || Displays.Count == 0;

    [RelayCommand(CanExecute = nameof(CanStartScreenTest))]
    private void StartScreenTest() => Launch(() => _windows.OpenScreen(SelectedDisplay?.Display));

    private void Launch(Action open)
    {
        try
        {
            open();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A device test window could not be opened.");
            _notifications.Error(Loc.T("The test could not open"), ex.Message);
        }
    }

    // ----- Links -----

    [RelayCommand]
    private void OpenMicrophonePrivacy() => Open(MicrophonePrivacyUri);

    [RelayCommand]
    private void OpenSoundSettings() => Open(SoundSettingsUri);

    [RelayCommand]
    private void GoToInputPage() => Navigate(typeof(InputPage));

    [RelayCommand]
    private void GoToAudioPage() => Navigate(typeof(AudioPage));

    [RelayCommand]
    private void GoToDisplaysPage() => Navigate(typeof(DisplaysPage));

    private void Open(string uri)
    {
        var result = ShellLauncher.Open(uri);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open"), result.Message);
        }
    }

    private void Navigate(Type page)
    {
        try
        {
            _navigation.Navigate(page);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open {Page}.", page.Name);
        }
    }
}

/// <summary>A screen in the screen-test picker.</summary>
public sealed class DisplayChoice
{
    public DisplayChoice(DisplayInfo display, int number)
    {
        Display = display;
        Number = number;
        Text = Loc.F("{0}. {1} ({2} x {3}, {4} Hz)", number, display.FriendlyName, display.Current.Width, display.Current.Height, display.Current.RefreshHz)
               + (display.IsPrimary ? " · " + Loc.T("main") : string.Empty);
    }

    public DisplayInfo Display { get; }

    public int Number { get; }

    public string Text { get; }

    public override string ToString() => Text;
}

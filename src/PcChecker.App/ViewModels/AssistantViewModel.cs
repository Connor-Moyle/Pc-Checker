using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Services;
using PcChecker.Claude;
using PcChecker.Core.Assistant;
using PcChecker.Core.Common;
using PcChecker.Core.Hardware;
using PcChecker.Core.Health;
using PcChecker.Core.Performance;
using PcChecker.Core.Sensors;
using PcChecker.Core.Settings;
using PcChecker.Core.Software;

namespace PcChecker.App.ViewModels;

/// <summary>
/// Ask Claude page: streaming chat about this PC with the latest scan results attached, plus the key/model/effort
/// settings and a transparent preview of exactly what will be sent.
/// </summary>
public sealed partial class AssistantViewModel : ViewModelBase
{
    public const string ConsoleUrl = "https://console.anthropic.com/";

    private readonly IPcAssistantService _assistant;
    private readonly ISecretStore _secrets;
    private readonly IHardwareInventoryService _hardware;
    private readonly IHealthService _health;
    private readonly IPerformanceAnalyzer _performance;
    private readonly ISensorService _sensors;
    private readonly ISoftwareInventoryService _software;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AssistantViewModel> _logger;
    private CancellationTokenSource? _streamCts;
    private bool _loadingSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetupPanel))]
    [NotifyPropertyChangedFor(nameof(CanType))]
    [NotifyPropertyChangedFor(nameof(ShowSuggestions))]
    [NotifyPropertyChangedFor(nameof(SetupTitle))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isConfigured;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetupPanel))]
    private bool _showSettings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand))]
    private string? _apiKeyInput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesEnvironmentKey))]
    [NotifyCanExecuteChangedFor(nameof(RemoveKeyCommand))]
    private bool _hasStoredKey;

    [ObservableProperty]
    private string _keyStatusText = string.Empty;

    [ObservableProperty]
    private AssistantModelOption _selectedModel = AssistantModelCatalog.Models[0];

    [ObservableProperty]
    private AssistantEffort _selectedEffort = AssistantEffort.Medium;

    [ObservableProperty]
    private bool _shareScanContext = true;

    [ObservableProperty]
    private string? _connectionStatus;

    [ObservableProperty]
    private bool _connectionOk;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTestingConnection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string? _input;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanType))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearChatCommand))]
    private bool _isStreaming;

    [ObservableProperty]
    private string? _contextPreview;

    [ObservableProperty]
    private string _contextSummary = string.Empty;

    [ObservableProperty]
    private bool _hasContext;

    public AssistantViewModel(ISettingsService settings, IPcAssistantService assistant, ISecretStore secrets,
        IHardwareInventoryService hardware, IHealthService health, IPerformanceAnalyzer performance, ISensorService sensors,
        ISoftwareInventoryService software, INotificationService notifications, IDialogService dialogs, ILogger<AssistantViewModel> logger)
        : base(settings)
    {
        _assistant = assistant;
        _secrets = secrets;
        _hardware = hardware;
        _health = health;
        _performance = performance;
        _sensors = sensors;
        _software = software;
        _notifications = notifications;
        _dialogs = dialogs;
        _logger = logger;

        Messages.CollectionChanged += OnMessagesChanged;
        LoadFromSettings(settings.Current);
        settings.Changed += (_, s) => DispatcherHelper.PostToUi(() => LoadFromSettings(s));
    }

    public ObservableCollection<AssistantChatMessage> Messages { get; } = [];

    public IReadOnlyList<AssistantModelOption> Models => AssistantModelCatalog.Models;

    public IReadOnlyList<AssistantEffort> Efforts { get; } = Enum.GetValues<AssistantEffort>();

    public IReadOnlyList<string> SuggestedQuestions => AssistantModelCatalog.SuggestedQuestions;

    public bool IsDemoMode => AppOptions.Mode == AppMode.Demo;

    public bool HasMessages => Messages.Count > 0;

    public bool ShowSuggestions => IsConfigured && !HasMessages;

    /// <summary>The settings card is always shown until a key exists, and on demand afterwards.</summary>
    public bool ShowSetupPanel => !IsConfigured || ShowSettings;

    public bool CanType => IsConfigured && !IsStreaming;

    public bool UsesEnvironmentKey => !HasStoredKey && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ClaudeAssistantService.EnvironmentKeyVariable));

    public string SetupTitle => IsConfigured ? "Claude settings" : "Set up Ask Claude";

    public string ContextCharBudget => $"Trimmed to about {Settings.Current.Claude.MaxContextChars:N0} characters.";

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        LoadFromSettings(Settings.Current);
        RefreshContext();

        var pending = AssistantPrompt.Take();
        if (pending is not null)
        {
            if (IsConfigured)
            {
                await SendQuestionAsync(pending);
            }
            else
            {
                Input = pending;
            }

            return;
        }

        if (IsDemoMode && Messages.Count == 0 && IsConfigured)
        {
            // Demo mode and screenshots: show a real-looking conversation without any typing.
            await SendQuestionAsync("Why is my PC slow?");
            await SendQuestionAsync("Is my disk healthy?");
        }
    }

    public override Task RefreshAsync(CancellationToken cancellationToken)
    {
        RefreshContext();
        return Task.CompletedTask;
    }

    /// <summary>Called by the page each time it is shown so questions queued by other pages are sent.</summary>
    public void OnPageShown()
    {
        if (!IsInitialized)
        {
            return; // InitializeAsync picks the pending question up.
        }

        RefreshContext();
        var pending = AssistantPrompt.Take();
        if (pending is null)
        {
            return;
        }

        if (IsConfigured && !IsStreaming)
        {
            _ = SendQuestionAsync(pending);
        }
        else
        {
            Input = pending;
        }
    }

    // ----- chat -----

    private bool CanSend => CanType && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var question = Input?.Trim();
        if (string.IsNullOrEmpty(question))
        {
            return;
        }

        Input = null;
        await SendQuestionAsync(question);
    }

    [RelayCommand]
    private Task AskSuggestedAsync(string? question) =>
        string.IsNullOrWhiteSpace(question) || !CanType ? Task.CompletedTask : SendQuestionAsync(question);

    [RelayCommand(CanExecute = nameof(IsStreaming))]
    private void Stop()
    {
        try
        {
            _streamCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool CanClearChat => HasMessages && !IsStreaming;

    [RelayCommand(CanExecute = nameof(CanClearChat))]
    private void ClearChat()
    {
        Messages.Clear();
        ErrorMessage = null;
    }

    [RelayCommand]
    private void CopyMessage(AssistantChatMessage? message)
    {
        if (message is null || !message.HasText)
        {
            return;
        }

        try
        {
            Clipboard.SetText(message.IsUser ? message.Text : message.DisplayText);
            _notifications.Success("Copied", "The message was copied to the clipboard.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
            ErrorMessage = "The message could not be copied: " + ex.Message;
        }
    }

    [RelayCommand]
    private void RefreshContext()
    {
        try
        {
            var context = BuildContext();
            HasContext = !context.IsEmpty;
            ContextPreview = HasContext
                ? AssistantPrompts.BuildContextBlock(context)
                : "No scan results yet. Open the Hardware, Health, Performance or Temperatures pages and run a scan; the summary appears here and is attached to your questions.";
            ContextSummary = string.Join("  ·  ",
                Describe("Hardware", context.HardwareSummary),
                Describe("Health", context.HealthSummary),
                Describe("Performance", context.PerformanceSummary),
                Describe("Temperatures", context.SensorSummary),
                Describe("Software", context.SoftwareSummary));
            OnPropertyChanged(nameof(ContextCharBudget));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not build the assistant context.");
            HasContext = false;
            ContextPreview = "The scan summary could not be built: " + ex.Message;
            ContextSummary = string.Empty;
        }

        static string Describe(string name, string? text) => (string.IsNullOrWhiteSpace(text) ? "○ " : "● ") + name;
    }

    private async Task SendQuestionAsync(string question)
    {
        if (IsStreaming)
        {
            return;
        }

        if (!IsConfigured)
        {
            Input = question;
            ErrorMessage = "Add your Anthropic API key first.";
            return;
        }

        ErrorMessage = null;
        var history = Messages.Where(m => !m.IsError && m.HasText).Select(m => m.ToMessage()).ToList();
        history.Add(AssistantMessage.User(question));
        Messages.Add(new AssistantChatMessage(AssistantMessage.UserRole, question));
        var reply = new AssistantChatMessage(AssistantMessage.AssistantRole, string.Empty) { IsStreaming = true };
        Messages.Add(reply);

        _streamCts?.Dispose();
        _streamCts = new CancellationTokenSource();
        var token = _streamCts.Token;
        IsStreaming = true;
        try
        {
            var context = ShareScanContext ? BuildContext() : null;
            await foreach (var chunk in _assistant.StreamChatAsync(history, context, token))
            {
                reply.Text += chunk;
            }

            if (!reply.HasText)
            {
                reply.Text = "Claude sent an empty answer. Please try again.";
                reply.IsError = true;
            }
        }
        catch (OperationCanceledException)
        {
            reply.Text = reply.HasText ? reply.Text + "\n\n(stopped)" : "Stopped before Claude answered.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Assistant chat failed.");
            reply.Text = ex is AssistantException ? ex.Message : AssistantErrorMapper.Describe(ex);
            reply.IsError = true;
        }
        finally
        {
            reply.IsStreaming = false;
            IsStreaming = false;
            _streamCts?.Dispose();
            _streamCts = null;
        }
    }

    private AssistantContext BuildContext()
    {
        var s = Settings.Current;
        return AssistantContextBuilder.Build(_hardware.Last, _health.Last, _performance.Last, _sensors.Latest, _software.Last,
            s.Claude.MaxContextChars, s.Logging.RedactSerialNumbers);
    }

    // ----- settings -----

    [RelayCommand]
    private void ToggleSettings() => ShowSettings = !ShowSettings;

    [RelayCommand]
    private void OpenConsole()
    {
        var result = ShellLauncher.Open(ConsoleUrl);
        if (!result.Success)
        {
            ErrorMessage = result.Message;
        }
    }

    private bool CanSaveKey => !string.IsNullOrWhiteSpace(ApiKeyInput);

    [RelayCommand(CanExecute = nameof(CanSaveKey))]
    private async Task SaveKeyAsync()
    {
        var key = ApiKeyInput?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (key.Length < 20 || key.Contains(' '))
        {
            ErrorMessage = "That does not look like an Anthropic API key. Keys are long and start with \"sk-ant-\".";
            return;
        }

        try
        {
            Settings.Current.Claude.ProtectedApiKey = _secrets.Protect(key);
            await Settings.SaveAsync();
            ApiKeyInput = null;
            LoadFromSettings(Settings.Current);
            _logger.LogInformation("Claude API key saved (protected with DPAPI).");
            _notifications.Success("Key saved", "Your API key is stored encrypted for this Windows account only.");
            await TestConnectionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The API key could not be stored.");
            ErrorMessage = "The key could not be stored: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasStoredKey))]
    private async Task RemoveKeyAsync()
    {
        var ok = await _dialogs.ConfirmAsync("Remove API key?",
            "The stored key will be deleted from this PC. Ask Claude stops working until you add a key again. Your chat history on this page is kept.",
            "Remove key", isDanger: true);
        if (!ok)
        {
            return;
        }

        try
        {
            Settings.Current.Claude.ProtectedApiKey = null;
            await Settings.SaveAsync();
            ConnectionStatus = null;
            ConnectionOk = false;
            LoadFromSettings(Settings.Current);
            _logger.LogInformation("Claude API key removed.");
            _notifications.Info("Key removed", "The API key was deleted from settings.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The API key could not be removed.");
            ErrorMessage = "The key could not be removed: " + ex.Message;
        }
    }

    private bool CanTestConnection => !IsTestingConnection;

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        ConnectionStatus = "Testing the connection…";
        ConnectionOk = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _assistant.TestConnectionAsync(cts.Token);
            ConnectionOk = result.Success;
            ConnectionStatus = result.Message;
        }
        catch (Exception ex)
        {
            ConnectionOk = false;
            ConnectionStatus = AssistantErrorMapper.Describe(ex);
        }
        finally
        {
            IsTestingConnection = false;
            IsConfigured = _assistant.IsConfigured;
        }
    }

    private void LoadFromSettings(AppSettings settings)
    {
        _loadingSettings = true;
        try
        {
            var claude = settings.Claude;
            HasStoredKey = !string.IsNullOrWhiteSpace(claude.ProtectedApiKey);
            IsConfigured = _assistant.IsConfigured;
            KeyStatusText = IsDemoMode && !HasStoredKey
                ? "Demo mode: answers are sample text and no key is needed."
                : HasStoredKey
                    ? "A key is saved on this PC, encrypted for your Windows account."
                    : UsesEnvironmentKey
                        ? "Using the key from the ANTHROPIC_API_KEY environment variable."
                        : "No key saved yet. Nothing is sent to Claude until you add one and ask a question.";
            SelectedModel = AssistantModelCatalog.Resolve(claude.Model);
            SelectedEffort = claude.Effort;
            ShareScanContext = claude.ShareScanContext;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    partial void OnSelectedModelChanged(AssistantModelOption value) => Persist(s => s.Claude.Model = value.Id);

    partial void OnSelectedEffortChanged(AssistantEffort value) => Persist(s => s.Claude.Effort = value);

    partial void OnShareScanContextChanged(bool value) => Persist(s => s.Claude.ShareScanContext = value);

    private void Persist(Action<AppSettings> apply)
    {
        if (_loadingSettings)
        {
            return;
        }

        apply(Settings.Current);
        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save assistant settings.");
            ErrorMessage = "Settings could not be saved: " + ex.Message;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(ShowSuggestions));
        ClearChatCommand.NotifyCanExecuteChanged();
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Assistant page error.");
}

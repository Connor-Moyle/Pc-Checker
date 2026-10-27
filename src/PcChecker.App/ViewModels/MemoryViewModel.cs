using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Common;
using PcChecker.Core.Memory;
using PcChecker.Core.Settings;

namespace PcChecker.App.ViewModels;

/// <summary>
/// View model for the Memory page: overview cards, module and page file tables, the XMP/EXPO card, the in-app
/// memory test, Windows Memory Diagnostic scheduling, memory profiles and the manual page file editor.
/// </summary>
public sealed partial class MemoryViewModel : ViewModelBase
{
    private readonly IMemoryService _memory;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly ILogger<MemoryViewModel> _logger;
    private CancellationTokenSource? _testCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverview))]
    private MemoryOverview? _overview;

    [ObservableProperty] private double _usedPercent;
    [ObservableProperty] private string _totalText = "—";
    [ObservableProperty] private string _usedText = "—";
    [ObservableProperty] private string _availableText = "—";
    [ObservableProperty] private string _commitText = "—";

    /// <summary>True after the memory diagnostic was scheduled; shows the shared "Restart needed" bar.</summary>
    [ObservableProperty] private bool _diagnosticRestartNeeded;

    [ObservableProperty] private string? _commitSubtitle;
    [ObservableProperty] private string _cachedText = "—";
    [ObservableProperty] private string _standbyText = "—";
    [ObservableProperty] private string _compressionText = "—";
    [ObservableProperty] private string _sysMainText = "—";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompressionButtonText))]
    private bool _memoryCompressionEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SysMainButtonText))]
    private bool _sysMainEnabled;
    [ObservableProperty] private string _kitSummary = "—";
    [ObservableProperty] private string _slotsText = "—";
    [ObservableProperty] private string _channelTitle = "—";
    [ObservableProperty] private string? _channelDetail;
    [ObservableProperty] private string _xmpState = "Unknown";
    [ObservableProperty] private string _xmpProfileName = "XMP/EXPO";
    [ObservableProperty] private string _xmpTitle = "—";
    [ObservableProperty] private string? _xmpGuidance;
    [ObservableProperty] private Severity _xmpSeverity = Severity.Low;
    [ObservableProperty] private string _pageFileSummary = "—";
    [ObservableProperty] private string _diagnosticText = "—";
    [ObservableProperty] private bool _diagnosticHasProblems;
    [ObservableProperty] private string? _capturedText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warningsText;

    [ObservableProperty] private int _testPercent = 25;
    [ObservableProperty] private int _testPasses = 1;
    [ObservableProperty] private string _testPlanText = "Choose how much of the free memory to test.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunMemoryTestCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopMemoryTestCommand))]
    [NotifyPropertyChangedFor(nameof(IsNotTestRunning))]
    private bool _isTestRunning;

    [ObservableProperty] private double _testProgress;
    [ObservableProperty] private string? _testStage;
    [ObservableProperty] private string? _testDetail;
    [ObservableProperty] private string? _testSummary;
    [ObservableProperty] private bool _testHasErrors;
    [ObservableProperty] private bool _testPassed;

    [ObservableProperty] private string _pageFileDrive = "C:";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageFileIsCustom))]
    private bool _pageFileSystemManaged = true;
    [ObservableProperty] private int _pageFileInitialMb;
    [ObservableProperty] private int _pageFileMaxMb;
    [ObservableProperty] private string? _pageFileHint;

    public MemoryViewModel(ISettingsService settings, IMemoryService memory, IDialogService dialogs, INotificationService notifications, ILogger<MemoryViewModel> logger)
        : base(settings)
    {
        _memory = memory;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;
        foreach (var profile in memory.Profiles)
        {
            Profiles.Add(new MemoryProfileItem(profile) { IsVisible = !profile.AdvancedOnly || IsAdvancedMode });
        }

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                foreach (var item in Profiles)
                {
                    item.IsVisible = !item.IsAdvancedOnly || IsAdvancedMode;
                }
            }
        };
    }

    public ObservableCollection<MemoryModuleRow> Modules { get; } = [];
    public ObservableCollection<PageFileRow> PageFiles { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<MemoryProfileItem> Profiles { get; } = [];
    public ObservableCollection<string> TestErrors { get; } = [];
    public ObservableCollection<string> PageFileDrives { get; } = ["C:"];

    public IReadOnlyList<int> TestPercentOptions { get; } = [10, 25, 50, 75];
    public IReadOnlyList<int> TestPassOptions { get; } = [1, 2, 3, 5];

    public bool HasOverview => Overview is not null;
    public string CompressionButtonText => MemoryCompressionEnabled ? Loc.T("Turn memory compression off") : Loc.T("Turn memory compression on");
    public string SysMainButtonText => SysMainEnabled ? Loc.T("Turn SysMain off") : Loc.T("Turn SysMain on");
    public bool IsNotTestRunning => !IsTestRunning;
    public bool PageFileIsCustom => !PageFileSystemManaged;
    public bool HasWarnings => !string.IsNullOrWhiteSpace(WarningsText);

    public override Task InitializeAsync(CancellationToken cancellationToken) => LoadAsync();

    public override Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync();

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    private Task LoadAsync() => RunBusyAsync(async ct =>
    {
        var overview = await _memory.GetOverviewAsync(ct);
        Apply(overview);
    }, "Reading memory details…");

    private void Apply(MemoryOverview o)
    {
        Overview = o;
        UsedPercent = o.UsedPercent;
        TotalText = ByteSize.Format(o.TotalBytes);
        UsedText = ByteSize.Format(o.UsedBytes);
        AvailableText = ByteSize.Format(o.AvailableBytes);
        CommitText = ByteSize.Format(o.CommitTotalBytes);
        CommitSubtitle = o.CommitLimitBytes > 0 ? $"of {ByteSize.Format(o.CommitLimitBytes)} limit (RAM + page file)" : null;
        CachedText = ByteSize.Format(o.CachedBytes);
        StandbyText = ByteSize.Format(o.StandbyBytes);
        MemoryCompressionEnabled = o.MemoryCompressionEnabled;
        SysMainEnabled = o.SysMainEnabled;
        CompressionText = !o.MemoryCompressionKnown ? "Unknown" : o.MemoryCompressionEnabled ? "On" : "Off";
        SysMainText = o.SysMainEnabled ? "Running" : "Stopped";
        KitSummary = MemoryRules.DescribeKit(o.Modules);
        SlotsText = o.Slots > 0 ? $"{o.SlotsUsed} of {o.Slots} slots used" : $"{o.SlotsUsed} module(s)";

        var xmp = MemoryRules.EvaluateXmp(o.Modules, (_memory as IMemoryTuning)?.PlatformVendor, PcChecker.Core.Hardware.LocalCpu.Name);
        XmpProfileName = xmp.ProfileName;
        XmpState = o.XmpState;
        XmpTitle = o.XmpState == "Enabled" && o.RatedMhz is { } rated ? $"Running at the rated {rated} MT/s" : xmp.Title;
        XmpGuidance = xmp.Guidance;
        XmpSeverity = o.XmpState switch { "Enabled" => Severity.Info, "Disabled" => Severity.Medium, _ => Severity.Low };

        var channels = MemoryRules.EvaluateChannels(o.Modules, o.Slots);
        ChannelTitle = channels.Title;
        ChannelDetail = channels.Detail;

        PageFileSummary = o.AutomaticPageFile
            ? "Windows chooses the size automatically (recommended for most PCs)."
            : o.PageFiles.Count == 0
                ? "No page file is configured."
                : $"Fixed size set by you: {string.Join(", ", o.PageFiles.Select(p => $"{p.Drive} {p.MaximumMb:N0} MB"))}";
        DiagnosticText = o.LastMemoryDiagnosticResult ?? "The Windows Memory Diagnostic has not been run yet.";
        DiagnosticHasProblems = DiagnosticText.Contains("problem", StringComparison.OrdinalIgnoreCase);
        CapturedText = $"Updated {DateTime.Now:t}";

        Modules.Clear();
        foreach (var module in o.Modules)
        {
            Modules.Add(MemoryModuleRow.From(module));
        }

        PageFiles.Clear();
        foreach (var pf in o.PageFiles)
        {
            PageFiles.Add(PageFileRow.From(pf));
        }

        Warnings.Clear();
        foreach (var warning in o.Warnings)
        {
            Warnings.Add(warning);
        }

        WarningsText = o.Warnings.Count == 0 ? null : string.Join("\n• ", o.Warnings.Prepend(string.Empty)).TrimStart('\n');

        var recommended = MemoryProfileCatalog.Recommended(o.TotalBytes);
        foreach (var item in Profiles)
        {
            item.IsActive = MemoryProfileCatalog.Matches(item.Profile, o);
            item.IsRecommended = item.Profile.Id == recommended.Id;
        }

        PageFileDrives.Clear();
        foreach (var drive in o.PageFiles.Select(p => p.Drive).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            PageFileDrives.Add(drive);
        }

        string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToUpperInvariant();
        if (!PageFileDrives.Contains(systemDrive))
        {
            PageFileDrives.Add(systemDrive);
        }

        var current = o.PageFiles.FirstOrDefault();
        PageFileDrive = PageFileRules.ChooseDrive(o.PageFiles, systemDrive);
        PageFileSystemManaged = o.AutomaticPageFile;
        var (initial, max) = PageFileRules.Recommend(o.TotalBytes);
        PageFileInitialMb = current is { SystemManaged: false } ? current.InitialMb : initial;
        PageFileMaxMb = current is { SystemManaged: false } ? current.MaximumMb : max;
        PageFileHint = $"Recommended fixed size for {TotalText} of RAM: {initial:N0}–{max:N0} MB.";
        UpdateTestPlan();
    }

    partial void OnTestPercentChanged(int value) => UpdateTestPlan();

    partial void OnTestPassesChanged(int value) => UpdateTestPlan();

    private void UpdateTestPlan()
    {
        if (Overview is null)
        {
            return;
        }

        long bytes = MemoryTestRunner.PlanBytes(Overview.AvailableBytes, TestPercent);
        TestPlanText = bytes <= 0
            ? "Not enough free memory for a test right now."
            : $"About {ByteSize.Format(bytes)} of the {ByteSize.Format(Overview.AvailableBytes)} currently free will be tested, {TestPasses} pass{(TestPasses == 1 ? string.Empty : "es")} of 4 patterns.";
    }

    private bool CanRunTest() => !IsTestRunning;

    [RelayCommand(CanExecute = nameof(CanRunTest))]
    private async Task RunMemoryTestAsync()
    {
        if (IsTestRunning)
        {
            return;
        }

        _testCts?.Dispose();
        _testCts = new CancellationTokenSource();
        var token = _testCts.Token;
        IsTestRunning = true;
        ErrorMessage = null;
        TestSummary = null;
        TestHasErrors = false;
        TestPassed = false;
        TestProgress = 0;
        TestStage = "Starting…";
        TestDetail = null;
        TestErrors.Clear();
        var progress = new Progress<ProgressUpdate>(p =>
        {
            TestStage = p.Stage;
            TestDetail = p.Detail;
            if (p.Percent is { } percent)
            {
                TestProgress = percent;
            }
        });

        try
        {
            var result = await _memory.RunMemoryTestAsync(TestPercent, TestPasses, progress, token);
            TestSummary = result.Summary;
            TestHasErrors = result.Errors > 0;
            TestPassed = result.Completed && result.Errors == 0;
            foreach (var detail in result.ErrorDetails)
            {
                TestErrors.Add(detail);
            }

            if (result.Errors > 0)
            {
                _notifications.Warning("Memory errors found", $"{result.Errors} error(s) in the quick memory test. See the Memory page for advice.");
            }
            else if (result.Completed)
            {
                _notifications.Success("Memory test passed", $"{ByteSize.Format(result.BytesTested)} tested with no errors.");
            }
        }
        catch (OperationCanceledException)
        {
            TestSummary = "The test was stopped before it finished.";
            TestStage = "Stopped";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory test failed.");
            ErrorMessage = "The memory test could not run: " + ex.Message;
        }
        finally
        {
            IsTestRunning = false;
            _testCts.Dispose();
            _testCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsTestRunning))]
    private void StopMemoryTest()
    {
        try
        {
            _testCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Report(OperationResult result, string successTitle)
    {
        if (result.Success)
        {
            _notifications.Success(successTitle, result.Message);
            if (result.RebootRequired)
            {
                _notifications.Info("Restart needed", "Restart the PC to finish applying this change.");
            }
        }
        else
        {
            _logger.LogWarning("Memory action failed: {Message}", result.Message);
            ErrorMessage = result.Message;
        }
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "Memory page operation failed.");
}

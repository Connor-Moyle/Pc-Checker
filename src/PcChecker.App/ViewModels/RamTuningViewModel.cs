using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.App.Views.Pages;
using PcChecker.Core.Common;
using PcChecker.Core.Crashes;
using PcChecker.Core.RamTuning;
using PcChecker.Core.Settings;
using Wpf.Ui;

namespace PcChecker.App.ViewModels;

/// <summary>
/// View model for the RAM tuning page: the detected memory, the personalised BIOS plan in four stages, the voltage
/// table, "Check my change" with its history, the stability tools and the quick memory check. Nothing on this page
/// changes a setting on the PC; the BIOS steps are done by the user.
/// </summary>
public sealed partial class RamTuningViewModel : ViewModelBase
{
    private readonly IRamTuningService _ram;
    private readonly ICrashService _crashes;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly ILogger<RamTuningViewModel> _logger;
    private RamTuningJournal _journal = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFacts))]
    private RamFacts? _facts;

    [ObservableProperty] private RamTuningPlan? _plan;
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private string _headline = string.Empty;
    [ObservableProperty] private string _headlineDetail = string.Empty;
    [ObservableProperty] private Severity _headlineSeverity = Severity.Info;
    [ObservableProperty] private string _headlineBadge = string.Empty;
    [ObservableProperty] private string _kitText = "—";
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _speedSubtitle = string.Empty;
    [ObservableProperty] private string _profileText = "—";
    [ObservableProperty] private string _channelText = "—";
    [ObservableProperty] private string _channelSubtitle = string.Empty;
    [ObservableProperty] private string _platformText = "—";
    [ObservableProperty] private string _boardText = "—";
    [ObservableProperty] private string _biosText = "—";
    [ObservableProperty] private string _chipText = "—";
    [ObservableProperty] private string? _notesText;
    [ObservableProperty] private string? _targetText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string? _warningsText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDimmTemps))]
    private string? _dimmTempText;

    [ObservableProperty] private bool _dimmTempWarm;
    [ObservableProperty] private string? _baselineText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSupportPageCommand))]
    private string? _supportUrl;

    public RamTuningViewModel(ISettingsService settings, IRamTuningService ram, ICrashService crashes, IDialogService dialogs,
        INotificationService notifications, INavigationService navigation, ILogger<RamTuningViewModel> logger)
        : base(settings)
    {
        _ram = ram;
        _crashes = crashes;
        _dialogs = dialogs;
        _notifications = notifications;
        _navigation = navigation;
        _logger = logger;
        foreach (var tool in RamStabilityTools.All(Loc.Current))
        {
            Tools.Add(new ToolRow(tool.Name, tool.WhatItDoes, tool.Duration, tool.Url, tool.Advanced));
        }

        foreach (var d in QuickMemoryCheck.Durations)
        {
            QuickDurations.Add(d.TotalMinutes >= 60 ? Loc.T("1 hour") : Loc.F("{0} minutes", (int)d.TotalMinutes));
        }

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAdvancedMode))
            {
                RebuildStages();
            }
            else if (e.PropertyName == nameof(QuickPercent))
            {
                UpdateQuickPlan();
            }
        };
    }

    public bool HasFacts => Facts is not null;
    public bool HasWarnings => !string.IsNullOrWhiteSpace(WarningsText);
    public bool HasDimmTemps => !string.IsNullOrWhiteSpace(DimmTempText);

    public ObservableCollection<RamModuleRow> Modules { get; } = [];
    public ObservableCollection<RamStageGroup> Stages { get; } = [];
    public ObservableCollection<VoltageRow> Voltages { get; } = [];
    public ObservableCollection<string> IfNoBoot { get; } = [];
    public ObservableCollection<ToolRow> Tools { get; } = [];

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RunBusyAsync(async ct =>
        {
            _journal = await _ram.LoadJournalAsync(ct);
            await DetectAsync(ct);
            if (_journal.Baseline is null && Facts is { } facts)
            {
                // The plan starts now: this is the "before" every later check compares crashes against.
                _journal.Baseline = RamVerifier.Baseline(facts, DateTimeOffset.Now);
                await _ram.SaveJournalAsync(_journal, ct);
            }

            ShowJournal();
            UpdateQuickPlan();
        }, Loc.T("Reading your memory…"));
    }

    public override Task RefreshAsync(CancellationToken cancellationToken) => RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(async ct =>
        {
            await DetectAsync(ct);
            UpdateQuickPlan();
        }, Loc.T("Reading your memory…"));
    }

    private async Task DetectAsync(CancellationToken ct)
    {
        var facts = await _ram.DetectAsync(Loc.Current, ct);
        var plan = RamPlanBuilder.Build(facts, DateTime.Today, Loc.Current);
        Facts = facts;
        Plan = plan;
        ShowFacts(facts, plan);
        RebuildStages();
    }

    private void ShowFacts(RamFacts facts, RamTuningPlan plan)
    {
        Subtitle = Loc.F("Checked {0}", facts.Captured.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        Headline = plan.Headline;
        HeadlineDetail = plan.HeadlineDetail;
        (HeadlineSeverity, HeadlineBadge) = plan.Tone switch
        {
            RamPlanTone.Good => (Severity.Low, Loc.T("Good")),
            RamPlanTone.Warning => (Severity.High, Loc.T("Free speed waiting")),
            _ => (Severity.Info, Loc.T("Worth a look")),
        };

        long total = facts.Modules.Sum(m => m.CapacityBytes ?? 0);
        KitText = facts.Modules.Count == 0 ? "—"
            : Loc.F("{0} ({1} × {2}) {3}", ByteSize.Format(total), facts.Modules.Count,
                ByteSize.Format(facts.ModuleBytes ?? 0), facts.MemoryType ?? string.Empty).Trim();
        SpeedText = facts.ConfiguredMts is { } c ? $"{c} MT/s" : "—";
        SpeedSubtitle = facts.RatedMts is { } r ? Loc.F("Rated {0} MT/s", r) : Loc.T("Rating unknown");
        ProfileText = facts.Xmp.State switch
        {
            "Enabled" => Loc.F("{0} on", plan.ProfileName),
            "Disabled" => Loc.F("{0} off", plan.ProfileName),
            _ => Loc.T("Unknown"),
        };
        ChannelText = facts.Layout.Mode switch
        {
            ChannelMode.Dual => Loc.T("Dual channel"),
            ChannelMode.Single => Loc.T("Single channel"),
            _ => Loc.T("Unknown"),
        };
        ChannelSubtitle = Loc.F("{0} of {1} slots used", facts.Modules.Count, facts.SlotCount) + (facts.Layout.MixedKits ? " · " + Loc.T("mixed kits") : string.Empty);
        PlatformText = RamPlanBuilder.Describe(facts.Platform, Loc.Current) + (facts.Platform.CpuName is { } cpu ? " · " + cpu : string.Empty);
        BoardText = facts.BoardName ?? Loc.T("Unknown board");
        BiosText = facts.BiosVersion is null ? Loc.T("Unknown")
            : facts.BiosDate is { } d ? Loc.F("{0}, released {1}", facts.BiosVersion, d.ToString("d", CultureInfo.CurrentCulture)) : facts.BiosVersion;
        var ic = RamPlanBuilder.MainIc(facts);
        ChipText = ic.Confidence switch
        {
            IcConfidence.Known => ic.Name,
            IcConfidence.Likely => Loc.F("{0} (likely)", ic.Name),
            _ => Loc.T("Unknown"),
        };
        SupportUrl = facts.SupportPage?.Url;
        TargetText = plan.Target is { } t
            ? Loc.F("Target: {0} MT/s ({1}); up to {2} MT/s if stable.", t.SafeMts, t.RatioText, t.StretchMts ?? t.SafeMts)
                + (t.FclkMhz is { } f ? " " + Loc.F("FCLK {0} MHz.", f) : string.Empty)
            : null;
        NotesText = plan.Notes.Count == 0 ? null : string.Join(Environment.NewLine, plan.Notes);
        WarningsText = facts.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, facts.Warnings);

        if (facts.DimmTemperatures.Count == 0)
        {
            DimmTempText = null;
            DimmTempWarm = false;
        }
        else
        {
            double hottest = facts.DimmTemperatures.Max(x => x.Celsius);
            DimmTempWarm = hottest >= RamTuningRules.DimmTightTimingsMaxC;
            DimmTempText = string.Join("  ·  ", facts.DimmTemperatures.Select(x => $"{x.Name} {x.Celsius:0} °C"))
                + "  —  " + (hottest >= RamTuningRules.DimmWarnC
                    ? Loc.T("Warm: add airflow over the memory and avoid tight timings.")
                    : DimmTempWarm
                        ? Loc.T("Fine for the profile, but too warm for a raised tREFI.")
                        : Loc.T("Cool enough for tighter timings."));
        }

        Modules.Clear();
        foreach (var m in facts.Modules)
        {
            Modules.Add(RamModuleRow.From(m));
        }

        Voltages.Clear();
        foreach (var v in plan.VoltageLimits)
        {
            Voltages.Add(VoltageRow.From(v));
        }

        IfNoBoot.Clear();
        int n = 1;
        foreach (var line in plan.IfItDoesNotBoot)
        {
            IfNoBoot.Add($"{n++}. {line}");
        }
    }

    private void RebuildStages()
    {
        Stages.Clear();
        if (Plan is not { } plan)
        {
            return;
        }

        int number = 1;
        foreach (RamStage stage in Enum.GetValues<RamStage>())
        {
            var steps = plan.StepsFor(stage, IsAdvancedMode).Select(s => new RamStepRow($"{number++}.", s)).ToList();
            if (steps.Count == 0)
            {
                continue;
            }

            var (title, subtitle) = stage switch
            {
                RamStage.UpdateBios => (Loc.T("Stage 1: Update the BIOS"), Loc.T("The safest start: newer BIOSes train memory better.")),
                RamStage.EnableProfile => (Loc.F("Stage 2: Turn on {0}", plan.ProfileName), Loc.T("One switch for the speed on the label.")),
                RamStage.PlatformTuning => (Loc.T("Stage 3: Tune for your processor"), Loc.T("Ratios, fabric speed and voltages that suit your platform.")),
                _ => (Loc.T("Stage 4: Manual timings (advanced)"), Loc.T("Small extra gains for experienced users. One change at a time.")),
            };
            bool expanded = stage is RamStage.UpdateBios or RamStage.EnableProfile || steps.Any(s => s.Step.State == RamStepState.ToDo);
            Stages.Add(new RamStageGroup(stage, title, subtitle, steps, expanded));
        }
    }

    private bool CanOpenSupportPage() => !string.IsNullOrWhiteSpace(SupportUrl);

    [RelayCommand(CanExecute = nameof(CanOpenSupportPage))]
    private void OpenSupportPage() => OpenLink(SupportUrl);

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var result = ShellLauncher.Open(url);
        if (!result.Success)
        {
            _notifications.Warning(Loc.T("Could not open"), result.Message);
        }
    }

    [RelayCommand]
    private void OpenCrashes() => _navigation.Navigate(typeof(CrashesPage));

    [RelayCommand]
    private void CopyPlan()
    {
        if (Plan is not { } plan)
        {
            return;
        }

        try
        {
            Clipboard.SetText(PlanText(plan));
            _notifications.Success(Loc.T("Plan copied"), Loc.T("Paste it into a note or print it to have it next to you in the BIOS."));
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _notifications.Warning(Loc.T("Could not copy"), ex.Message);
        }
    }

    private string PlanText(RamTuningPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine(plan.Headline);
        sb.AppendLine(plan.HeadlineDetail);
        sb.AppendLine();
        foreach (var group in Stages)
        {
            sb.AppendLine(group.Title);
            foreach (var row in group.Steps)
            {
                sb.AppendLine($"{row.Number} {row.Title} [{row.StateText}]");
                if (row.HasMenuPath)
                {
                    sb.AppendLine("   " + Loc.T("Where:") + " " + row.MenuPath);
                }

                sb.AppendLine("   " + Loc.T("Safe range:") + " " + row.SafeRange);
            }

            sb.AppendLine();
        }

        sb.AppendLine(Loc.T("Voltage limits"));
        foreach (var v in Voltages)
        {
            sb.AppendLine($"- {v.Name}: {v.Typical}, {Loc.T("daily max")} {v.DailyMax}");
        }

        sb.AppendLine();
        sb.AppendLine(Loc.T("If it does not boot"));
        foreach (var line in IfNoBoot)
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    protected override void OnBusyError(Exception exception) => _logger.LogError(exception, "RAM tuning page failed");
}

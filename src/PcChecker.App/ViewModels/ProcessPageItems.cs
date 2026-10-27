using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.Core.Processes;

namespace PcChecker.App.ViewModels;

/// <summary>One entry of the "Columns" menu: a column that can be shown or hidden.</summary>
public sealed partial class ProcessColumnOption : ObservableObject
{
    private readonly Action<ProcessColumnOption> _changed;
    private bool _isVisible;

    public ProcessColumnOption(string id, string header, string description, bool canHide, bool isVisible, Action<ProcessColumnOption> changed)
    {
        Id = id;
        Header = header;
        Description = description;
        CanHide = canHide;
        _isVisible = isVisible;
        _changed = changed;
    }

    public string Id { get; }

    public string Header { get; }

    public string Description { get; }

    public bool CanHide { get; }

    /// <summary>Ticked in the Columns menu; a change is saved and shown straight away.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                _changed(this);
            }
        }
    }

    /// <summary>Sets the value without telling the page (used when switching views).</summary>
    public void Load(bool visible)
    {
        if (_isVisible != visible)
        {
            _isVisible = visible;
            OnPropertyChanged(nameof(IsVisible));
        }
    }
}

/// <summary>One update speed in the menu.</summary>
public sealed partial class UpdateSpeedOption : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public UpdateSpeedOption(ProcessUpdateSpeed speed, string label, string description)
    {
        Speed = speed;
        Label = label;
        Description = description;
    }

    public ProcessUpdateSpeed Speed { get; }

    public string Label { get; }

    public string Description { get; }
}

/// <summary>One logical processor in the affinity dialog.</summary>
public sealed partial class AffinityProcessor : ObservableObject
{
    private readonly Action _changed;

    [ObservableProperty]
    private bool _isChecked;

    public AffinityProcessor(int index, bool isChecked, Action changed)
    {
        Index = index;
        _isChecked = isChecked;
        _changed = changed;
    }

    public int Index { get; }

    public string Label => Loc.F("CPU {0}", Index.ToString(CultureInfo.InvariantCulture));

    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>Body of the "Set affinity" dialog: one checkbox per logical processor plus "All processors".</summary>
public sealed partial class AffinityChoices : ObservableObject
{
    private bool _updating;

    [ObservableProperty]
    private bool? _allChecked;

    [ObservableProperty]
    private string _summary = string.Empty;

    public AffinityChoices(string programName, ulong current, int processorCount)
    {
        ProgramName = programName;
        ProcessorCount = processorCount;
        var chosen = AffinityMath.Processors(current, processorCount).ToHashSet();
        foreach (int i in Enumerable.Range(0, Math.Min(processorCount, AffinityMath.MaxProcessors)))
        {
            Processors.Add(new AffinityProcessor(i, chosen.Contains(i), Refresh));
        }

        Refresh();
    }

    public string ProgramName { get; }

    public int ProcessorCount { get; }

    public string Intro => Loc.F("Choose which processors {0} may run on. Fewer processors can make it slower; it resets when the program restarts.", ProgramName);

    public ObservableCollection<AffinityProcessor> Processors { get; } = [];

    public ulong Mask => AffinityMath.ToMask(Processors.Where(p => p.IsChecked).Select(p => p.Index));

    /// <summary>
    /// "All processors" clicked: ticks every processor, or unticks them all when every one was ticked. A plain
    /// two-state checkbox would go from the mixed state to unticked and clear every processor.
    /// </summary>
    [RelayCommand]
    private void ToggleAll()
    {
        bool target = AllChecked != true;
        _updating = true;
        foreach (var processor in Processors)
        {
            processor.IsChecked = target;
        }

        _updating = false;
        Refresh();
    }

    private void Refresh()
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        int count = Processors.Count(p => p.IsChecked);
        AllChecked = count == Processors.Count ? true : count == 0 ? false : null;
        Summary = count == 0
            ? Loc.T("Pick at least one processor.")
            : AffinityMath.Describe(Mask, ProcessorCount);
        _updating = false;
    }
}

/// <summary>Body of the "Run new task" dialog.</summary>
public sealed partial class RunTaskChoices : ObservableObject
{
    private readonly Func<Task<string?>> _browse;

    [ObservableProperty]
    private string _command = string.Empty;

    [ObservableProperty]
    private bool _asAdministrator;

    public RunTaskChoices(IEnumerable<string> recent, Func<Task<string?>> browse)
    {
        _browse = browse;
        foreach (string item in recent)
        {
            Recent.Add(item);
        }

        _command = Recent.FirstOrDefault() ?? string.Empty;
    }

    public ObservableCollection<string> Recent { get; } = [];

    [RelayCommand]
    private async Task BrowseAsync()
    {
        string? file = await _browse();
        if (!string.IsNullOrWhiteSpace(file))
        {
            Command = file.Contains(' ', StringComparison.Ordinal) ? "\"" + file + "\"" : file;
        }
    }
}

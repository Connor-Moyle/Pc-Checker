using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PcChecker.App.Helpers;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Settings;
using PcChecker.Core.Shell;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace PcChecker.App.ViewModels;

/// <summary>One line in the quick search list.</summary>
public sealed record QuickSearchRow(SearchItem Item, string Title, string Subtitle, string KindText, SymbolRegular Symbol, bool IsRecent)
{
    public bool HasSubtitle => Subtitle.Length > 0;
}

/// <summary>
/// The quick search palette (Ctrl+K, or the search box at the top of the navigation pane): searches every page and
/// tab, every option on Settings and the quick actions, and opens the chosen one. Recently opened items are kept in
/// settings and listed first.
/// </summary>
public sealed partial class QuickSearchViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly INavigationService _navigation;
    private readonly IPageService _pages;
    private readonly QuickActionRunner _actions;
    private readonly ILogger<QuickSearchViewModel> _logger;
    private IReadOnlyList<SearchItem>? _index;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private QuickSearchRow? _selected;

    [ObservableProperty]
    private bool _hasNoResults;

    public QuickSearchViewModel(ISettingsService settings, INavigationService navigation, IPageService pages, QuickActionRunner actions,
        ILogger<QuickSearchViewModel> logger)
    {
        _settings = settings;
        _navigation = navigation;
        _pages = pages;
        _actions = actions;
        _logger = logger;
    }

    public ObservableCollection<QuickSearchRow> Results { get; } = [];

    /// <summary>Raised after the palette opens, so the view can put the cursor in the search box.</summary>
    public event EventHandler? Opened;

    /// <summary>Every page, setting and action; built on first use from <see cref="NavigationCatalog"/> and the Core catalogs.</summary>
    public IReadOnlyList<SearchItem> Index => _index ??= QuickSearchIndex.Build(
        NavigationCatalog.Pages.Select(p => new SearchPageInfo(p.Section, p.Title, p.FileName)),
        SettingsSearchCatalog.All,
        QuickActionCatalog.All);

    [RelayCommand]
    private void Open()
    {
        var general = _settings.Current.General;
        general.RecentSearchItems = RecentItems.Prune(general.RecentSearchItems, Index.Select(i => i.Id));
        if (Query.Length == 0)
        {
            UpdateResults();
        }
        else
        {
            Query = string.Empty; // updates the results
        }

        IsOpen = true;
        Opened?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    partial void OnQueryChanged(string value) => UpdateResults();

    /// <summary>Moves the highlight: one step wraps around at the ends, a longer jump (Page Up/Down) stops at them.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0 || delta == 0)
        {
            return;
        }

        int index = Selected is null ? -1 : Results.IndexOf(Selected);
        if (index < 0)
        {
            index = delta > 0 ? 0 : Results.Count - 1;
        }
        else if (Math.Abs(delta) == 1)
        {
            index = (index + delta + Results.Count) % Results.Count;
        }
        else
        {
            index = Math.Clamp(index + delta, 0, Results.Count - 1);
        }

        Selected = Results[index];
    }

    /// <summary>Opens <paramref name="row"/> (or the highlighted row), closes the palette and remembers it as recent.</summary>
    [RelayCommand]
    private async Task ActivateAsync(QuickSearchRow? row)
    {
        row ??= Selected;
        if (row is null)
        {
            return;
        }

        IsOpen = false;
        Remember(row.Item.Id);
        try
        {
            await OpenItemAsync(row.Item);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quick search could not open {Item}.", row.Item.Id);
        }
    }

    private Task OpenItemAsync(SearchItem item)
    {
        if (item.Kind == SearchItemKind.Action)
        {
            return QuickSearchIndex.ActionIdOf(item.Id) is { } actionId ? _actions.RunAsync(actionId) : Task.CompletedTask;
        }

        var entry = NavigationCatalog.Pages.FirstOrDefault(p => p.FileName == item.PageKey);
        if (entry is null)
        {
            return Task.CompletedTask;
        }

        _navigation.Navigate(entry.PageType);
        if (item.Kind == SearchItemKind.Setting && item.Anchor is { } anchor && _pages.GetPage(entry.PageType) is { } page)
        {
            SettingRevealer.Reveal(page, anchor);
        }

        return Task.CompletedTask;
    }

    private void UpdateResults()
    {
        var results = QuickSearchRanker.Search(Query, Index, _settings.Current.General.RecentSearchItems, Loc.T);
        Results.Clear();
        foreach (var result in results)
        {
            Results.Add(ToRow(result));
        }

        Selected = Results.FirstOrDefault();
        HasNoResults = Results.Count == 0;
    }

    private void Remember(string id)
    {
        var general = _settings.Current.General;
        general.RecentSearchItems = RecentItems.Push(general.RecentSearchItems, id);
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not save the recent quick search items.");
        }
    }

    private static QuickSearchRow ToRow(SearchResult result)
    {
        var item = result.Item;
        string pageTitle = item.PageTitle is null ? string.Empty : Loc.T(item.PageTitle);
        var (kind, subtitle, symbol) = item.Kind switch
        {
            SearchItemKind.Page => (Loc.T("Page"), result.Group == result.Title ? string.Empty : result.Group, SectionSymbol(item.PageKey)),
            SearchItemKind.Setting => (Loc.T("Setting"), result.Group.Length == 0 || result.Group == pageTitle ? pageTitle : pageTitle + " › " + result.Group,
                SymbolRegular.Settings24),
            _ => (Loc.T("Action"), pageTitle, SymbolRegular.Flash24),
        };
        return new QuickSearchRow(item, result.Title, subtitle, kind, symbol, result.IsRecent);
    }

    private static SymbolRegular SectionSymbol(string? pageKey)
    {
        var entry = NavigationCatalog.Pages.FirstOrDefault(p => p.FileName == pageKey);
        return entry is not null && NavigationCatalog.SectionOf(entry.PageType) is { } section ? section.Icon : SymbolRegular.Document24;
    }
}

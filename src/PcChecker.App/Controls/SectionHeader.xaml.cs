using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PcChecker.App.Localization;
using PcChecker.App.Services;
using PcChecker.Core.Shell;

namespace PcChecker.App.Controls;

/// <summary>
/// Page header: title with a "?" help button, one-line subtitle and an optional action area on the right. The help
/// comes from <see cref="HelpCatalog"/>, found through the page the header sits on (or <see cref="HelpKey"/>).
/// </summary>
public partial class SectionHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(SectionHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(nameof(Actions), typeof(object), typeof(SectionHeader), new PropertyMetadata(null));

    /// <summary>The page key to show help for; empty means "the page this header is on".</summary>
    public static readonly DependencyProperty HelpKeyProperty =
        DependencyProperty.Register(nameof(HelpKey), typeof(string), typeof(SectionHeader), new PropertyMetadata(null, (d, _) => ((SectionHeader)d).ResolveHelp()));

    private PageHelp? _help;

    public SectionHeader()
    {
        InitializeComponent();
        Loaded += (_, _) => ResolveHelp();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public string? HelpKey
    {
        get => (string?)GetValue(HelpKeyProperty);
        set => SetValue(HelpKeyProperty, value);
    }

    /// <summary>True when this header has help to show.</summary>
    public bool HasHelp => _help is not null;

    /// <summary>Opens the help flyout (F1). With <paramref name="focus"/> the keyboard moves into it, so Esc closes it.</summary>
    public bool OpenHelp(bool focus)
    {
        ResolveHelp();
        if (_help is null)
        {
            return false;
        }

        _focusOnOpen = focus;
        HelpPopup.IsOpen = true;
        return true;
    }

    private bool _focusOnOpen;

    private void ResolveHelp()
    {
        var key = string.IsNullOrWhiteSpace(HelpKey) ? PageHelpLocator.KeyOf(PageHelpLocator.PageOf(this)?.GetType()) : HelpKey;
        _help = HelpCatalog.Find(key);
        HelpButton.Visibility = _help is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnHelpClick(object sender, RoutedEventArgs e) => OpenHelp(focus: true);

    private void OnHelpOpened(object? sender, EventArgs e)
    {
        if (_help is null)
        {
            return;
        }

        HelpTitle.Text = Loc.F("About {0}", string.IsNullOrWhiteSpace(Title) ? Loc.T("this page") : Title);
        HelpWhat.Text = Loc.T(_help.WhatItDoes);
        HelpFirstStepLabel.Text = Loc.T("Safest first step");
        HelpFirstStep.Text = Loc.T(_help.FirstStep);
        HelpDocsLink.Content = Loc.T("Read the full guide");
        HelpDocsLink.NavigateUri = _help.DocsUrl;
        HelpDocsLink.ToolTip = _help.DocsUrl;
        HelpShortcutsButton.Content = Loc.T("Keyboard shortcuts");
        HelpShortcutsButton.ToolTip = Loc.T("Every keyboard shortcut (press ? anywhere)");
        if (_focusOnOpen)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => HelpDocsLink.Focus());
        }
    }

    private void OnHelpKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HelpPopup.IsOpen = false;
            HelpButton.Focus();
            e.Handled = true;
        }
    }

    private void OnShortcutsClick(object sender, RoutedEventArgs e)
    {
        HelpPopup.IsOpen = false;
        ShellRequests.ShowShortcuts();
    }
}

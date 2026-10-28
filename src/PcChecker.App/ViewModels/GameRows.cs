using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.App.Localization;
using PcChecker.Core.Common;
using PcChecker.Core.Games;

namespace PcChecker.App.ViewModels;

/// <summary>One game card on the Games page.</summary>
public sealed partial class GameCardRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArt))]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private ImageSource? _art;

    [ObservableProperty]
    private bool _artIsIcon;

    public GameCardRow(InstalledGame game)
    {
        Game = game;
        Initials = MakeInitials(game.Name);
    }

    public InstalledGame Game { get; private set; }

    public string Name => Game.Name;

    public string Initials { get; }

    public GameLauncher Launcher => Game.Launcher;

    public string LauncherText => GameLibraryMath.LauncherName(Game.Launcher);

    public string SizeText => Game.SizeBytes is { } size ? ByteSize.Format(size) : Loc.T("Measuring…");

    public string DriveText => Game.Drive ?? "—";

    public string LastPlayedText => GameText.LastPlayed(Game.LastPlayed);

    public string LastPlayedToolTip => Game.LastPlayed is { } played
        ? Loc.F("Last played {0:f}", played.LocalDateTime)
        : Loc.T("This launcher does not record when a game was last played.");

    public string? NoteText => Game.Note is null ? null : Loc.T(Game.Note);

    public bool HasNote => Game.Note is not null;

    public string FolderText => Game.InstallPath ?? Loc.T("Folder not known");

    public bool HasArt => Art is not null;

    public bool ShowPlaceholder => Art is null;

    public bool CanPlay => Game.Play is not null;

    public bool CanVerify => Game.Verify is not null;

    public bool CanUninstall => Game.Uninstall is not null;

    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(Game.InstallPath);

    public bool CanScanDlls => !string.IsNullOrWhiteSpace(Game.InstallPath);

    public bool CanMakeProfile => !string.IsNullOrWhiteSpace(Game.ExePath);

    public bool CanCleanShaderCache => Game.Launcher == GameLauncher.Steam && Game.ShaderCacheBytes is > 0;

    public string ShaderCacheText => Game.ShaderCacheBytes is > 0
        ? Loc.F("Clean shader cache ({0})", ByteSize.Format(Game.ShaderCacheBytes.Value))
        : Loc.T("Clean shader cache");

    public string PlayToolTip => GameText.Note(Game.Play) ?? Loc.T("Starts the game.");

    public string VerifyToolTip => GameText.Note(Game.Verify) ?? Loc.T("This launcher has no way to check the game's files.");

    public string UninstallToolTip => GameText.Note(Game.Uninstall) ?? Loc.T("Remove the game from its launcher.");

    /// <summary>What a screen reader says for the card.</summary>
    public string AccessibleName => Loc.F("{0}, {1}, {2} on {3}", Name, LauncherText, SizeText, DriveText);

    /// <summary>Swaps in a newer copy of the same game (a measured size) and tells the card.</summary>
    public void Update(InstalledGame game)
    {
        Game = game;
        OnPropertyChanged(string.Empty);
    }

    private static string MakeInitials(string name)
    {
        var words = name.Split([' ', '-', ':', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0]) && !w.Equals("the", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var letters = words.Take(2).Select(w => char.ToUpper(w[0], CultureInfo.CurrentCulture));
        var text = new string(letters.ToArray());
        return text.Length > 0 ? text : "?";
    }
}

/// <summary>A choice in the launcher filter.</summary>
public sealed record LauncherFilterOption(GameLauncher? Launcher, string Label);

/// <summary>A choice in the drive filter.</summary>
public sealed record DriveFilterOption(string? Drive, string Label);

/// <summary>A choice in the sort drop-down.</summary>
public sealed record GameSortOption(GameSortOrder Order, string Label);

/// <summary>One drive's share of the library, shown under the totals.</summary>
public sealed record DriveTotalRow(string Drive, string Text);

/// <summary>Translated text for the Games page.</summary>
internal static class GameText
{
    public static string LastPlayed(DateTimeOffset? played)
    {
        var days = GameLibraryMath.DaysSince(played, DateTimeOffset.Now);
        return days switch
        {
            null => Loc.T("Not recorded"),
            0 => Loc.T("Today"),
            1 => Loc.T("Yesterday"),
            < 14 => Loc.F("{0} days ago", days.Value),
            < 60 => Loc.F("{0} weeks ago", days.Value / 7),
            < 365 => Loc.F("{0} months ago", days.Value / 30),
            _ => played!.Value.LocalDateTime.ToString("d", CultureInfo.CurrentCulture),
        };
    }

    public static string? Note(GameCommand? command) => command?.Note is { } note ? Loc.T(note) : null;
}

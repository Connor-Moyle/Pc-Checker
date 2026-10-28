using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcChecker.App.Localization;
using PcChecker.Core.DeviceTests;

namespace PcChecker.App.ViewModels;

/// <summary>State of one key on the on-screen keyboard.</summary>
public enum KeyCapState
{
    Untested,
    Tested,
    Held,
}

/// <summary>
/// The keyboard test window: an on-screen full-size keyboard that lights the keys you hold, marks the ones you have
/// tested, and counts how many keys the keyboard reports at once (rollover). Keys arrive from the window as raw
/// key messages (<see cref="KeyDown"/>, <see cref="KeyUp"/>), so nothing is typed anywhere.
/// </summary>
public sealed partial class KeyboardTestViewModel : ObservableObject
{
    /// <summary>Size of one key unit in the drawing, before the window scales it to fit.</summary>
    public const double Unit = 44;

    private readonly KeyRolloverTracker _tracker = new();
    private readonly Dictionary<int, KeyCapItem> _byCode = [];
    private readonly List<string> _otherKeys = [];

    [ObservableProperty] private string _testedText = string.Empty;
    [ObservableProperty] private string _heldText = string.Empty;
    [ObservableProperty] private string _rolloverText = string.Empty;
    [ObservableProperty] private string _rolloverAdvice = string.Empty;
    [ObservableProperty] private string _lastKeyText = Loc.T("Press any key.");
    [ObservableProperty] private string? _otherKeysText;

    public KeyboardTestViewModel()
    {
        foreach (var key in KeyboardLayout.FullSize)
        {
            var item = new KeyCapItem(key, Unit);
            Keys.Add(item);
            _byCode[key.Code] = item;
        }

        UpdateTexts();
    }

    public ObservableCollection<KeyCapItem> Keys { get; } = [];

    public double LayoutWidth => KeyboardLayout.TotalWidth * Unit;

    public double LayoutHeight => KeyboardLayout.TotalHeight * Unit;

    /// <summary>Relabels letter and symbol keys with the names Windows uses for the current keyboard language.</summary>
    public void ApplyKeyNames(Func<int, string?> nameOf)
    {
        foreach (var item in Keys)
        {
            // Only the one-character keys of the main block change with the language; the number pad and the
            // extended keys are the same everywhere.
            var key = item.Key;
            if (key.Label.Length != 1 || key.Code >= KeyCodes.Extended || key.X >= 18.5)
            {
                continue;
            }

            string? name = nameOf(item.Key.Code);
            if (!string.IsNullOrWhiteSpace(name) && name.Length == 1)
            {
                item.Label = name.ToUpperInvariant();
            }
        }
    }

    /// <summary>A key went down. <paramref name="name"/> is Windows' name for it, used for keys not on the layout.</summary>
    public void KeyDown(int code, string? name)
    {
        bool fresh = _tracker.Press(code);
        if (_byCode.TryGetValue(code, out var item))
        {
            item.State = KeyCapState.Held;
            LastKeyText = Loc.F("Last key: {0} ({1})", item.Label, KeyCodes.Describe(code));
        }
        else if (fresh)
        {
            string label = string.IsNullOrWhiteSpace(name) ? KeyCodes.Describe(code) : name!;
            if (!_otherKeys.Contains(label))
            {
                _otherKeys.Add(label);
                OtherKeysText = Loc.F("Other keys pressed: {0}", string.Join(", ", _otherKeys));
            }

            LastKeyText = Loc.F("Last key: {0} ({1})", label, KeyCodes.Describe(code));
        }

        UpdateTexts();
    }

    public void KeyUp(int code, string? name)
    {
        bool wasHeld = _tracker.IsHeld(code);
        _tracker.Release(code);
        if (_byCode.TryGetValue(code, out var item))
        {
            item.State = KeyCapState.Tested;
            if (!wasHeld)
            {
                LastKeyText = Loc.F("Last key: {0} ({1})", item.Label, KeyCodes.Describe(code));
            }
        }

        UpdateTexts();
    }

    /// <summary>The window lost focus: key releases will not arrive, so stop showing keys as held.</summary>
    public void FocusLost()
    {
        foreach (int code in _tracker.Held.ToList())
        {
            if (_byCode.TryGetValue(code, out var item))
            {
                item.State = KeyCapState.Tested;
            }
        }

        _tracker.ReleaseAll();
        UpdateTexts();
    }

    [RelayCommand]
    private void Reset()
    {
        _tracker.Reset();
        _otherKeys.Clear();
        OtherKeysText = null;
        foreach (var item in Keys)
        {
            item.State = KeyCapState.Untested;
        }

        LastKeyText = Loc.T("Press any key.");
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        TestedText = Loc.F("{0} of {1} keys tested", _tracker.TestedOf(_byCode.Keys), Keys.Count);
        HeldText = Loc.F("{0} held now", _tracker.HeldCount);
        RolloverText = Loc.F("Most at once: {0}", _tracker.MaxHeld);
        RolloverAdvice = _tracker.Verdict switch
        {
            RolloverVerdict.Limited => Loc.T("Only 2 or 3 keys registered together. If a key you held did not light up, the keyboard blocked it (ghosting); some key combinations in games may not work."),
            RolloverVerdict.Fair => Loc.T("4 or 5 keys at once: fine for typing, tight for some games."),
            RolloverVerdict.Good => Loc.T("6 or more keys at once: fine for games. Many USB keyboards stop at 6."),
            RolloverVerdict.Excellent => Loc.T("10 or more keys at once: n-key rollover, every key counts."),
            _ => Loc.T("Hold several keys together (for example A, S, D, F, J, K, L) to see how many the keyboard reports at once."),
        };
    }
}

/// <summary>A key drawn on the on-screen keyboard, with its position in pixels.</summary>
public sealed partial class KeyCapItem : ObservableObject
{
    private const double Gap = 4;

    [ObservableProperty] private KeyCapState _state;
    [ObservableProperty] private string _label;

    public KeyCapItem(KeyCap key, double unit)
    {
        Key = key;
        _label = key.Label;
        Left = key.X * unit;
        Top = key.Y * unit;
        Width = key.Width * unit - Gap;
        Height = key.Height * unit - Gap;
    }

    public KeyCap Key { get; }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }
}

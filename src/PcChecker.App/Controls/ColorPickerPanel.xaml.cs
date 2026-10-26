using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PcChecker.Core.Lighting;

namespace PcChecker.App.Controls;

/// <summary>
/// Hue / saturation / brightness sliders, a hex box and quick-pick swatches. Bind <see cref="SelectedColor"/> two-way.
/// </summary>
public partial class ColorPickerPanel : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(ColorPickerPanel),
            new FrameworkPropertyMetadata(Color.FromRgb(0x4C, 0xC2, 0xFF), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    /// <summary>Raised after the user picks a colour (sliders, hex or swatch), not on programmatic changes.</summary>
    public static readonly RoutedEvent ColorPickedEvent =
        EventManager.RegisterRoutedEvent(nameof(ColorPicked), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ColorPickerPanel));

    private bool _updating;

    public ColorPickerPanel()
    {
        InitializeComponent();
        Swatches.ItemsSource = DefaultSwatches;
        SyncFromColor(SelectedColor);
    }

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public event RoutedEventHandler ColorPicked
    {
        add => AddHandler(ColorPickedEvent, value);
        remove => RemoveHandler(ColorPickedEvent, value);
    }

    /// <summary>A small, friendly palette; the first entries match the app's accent colours.</summary>
    public static IReadOnlyList<Swatch> DefaultSwatches { get; } =
    [
        new("Sky blue", "#4CC2FF"),
        new("Cyan", "#00E5FF"),
        new("Green", "#22C55E"),
        new("Lime", "#A3E635"),
        new("Yellow", "#FACC15"),
        new("Orange", "#F97316"),
        new("Red", "#EF4444"),
        new("Pink", "#EC4899"),
        new("Purple", "#A78BFA"),
        new("Violet", "#7C3AED"),
        new("Blue", "#2563EB"),
        new("Warm white", "#FFE4C4"),
        new("White", "#FFFFFF"),
        new("Off", "#000000"),
    ];

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorPickerPanel panel && !panel._updating)
        {
            panel.SyncFromColor((Color)e.NewValue);
        }
    }

    private void SyncFromColor(Color color)
    {
        _updating = true;
        try
        {
            var (h, s, v) = RgbColorMath.ToHsv(new RgbColor(color.R, color.G, color.B));
            // Keep the hue slider where it was for greys so the slider does not jump to red.
            if (s > 0.0001 && v > 0.0001)
            {
                HueSlider.Value = h;
            }

            SaturationSlider.Value = s * 100;
            ValueSlider.Value = v * 100;
            HexBox.Text = new RgbColor(color.R, color.G, color.B).ToHex();
            Preview.Background = new SolidColorBrush(color);
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || HueSlider is null || SaturationSlider is null || ValueSlider is null)
        {
            return;
        }

        var rgb = RgbColorMath.FromHsv(HueSlider.Value, SaturationSlider.Value / 100, ValueSlider.Value / 100);
        Commit(Color.FromRgb(rgb.R, rgb.G, rgb.B));
    }

    private void OnHexCommitted(object sender, RoutedEventArgs e) => TryCommitHex();

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryCommitHex();
            e.Handled = true;
        }
    }

    private void TryCommitHex()
    {
        if (RgbColorMath.TryParseHex(HexBox.Text, out var rgb))
        {
            Commit(Color.FromRgb(rgb.R, rgb.G, rgb.B));
        }
        else
        {
            HexBox.Text = new RgbColor(SelectedColor.R, SelectedColor.G, SelectedColor.B).ToHex();
        }
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Swatch swatch })
        {
            Commit(swatch.Color);
        }
    }

    private void Commit(Color color)
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        try
        {
            SelectedColor = color;
            HexBox.Text = new RgbColor(color.R, color.G, color.B).ToHex();
            Preview.Background = new SolidColorBrush(color);
            var (h, s, v) = RgbColorMath.ToHsv(new RgbColor(color.R, color.G, color.B));
            if (s > 0.0001 && v > 0.0001)
            {
                HueSlider.Value = h;
            }

            SaturationSlider.Value = s * 100;
            ValueSlider.Value = v * 100;
        }
        finally
        {
            _updating = false;
        }

        RaiseEvent(new RoutedEventArgs(ColorPickedEvent, this));
    }

    /// <summary>A named quick-pick colour.</summary>
    public sealed class Swatch
    {
        public Swatch(string name, string hex)
        {
            Name = name;
            var rgb = RgbColor.FromHex(hex);
            Color = Color.FromRgb(rgb.R, rgb.G, rgb.B);
            Brush = new SolidColorBrush(Color);
            Brush.Freeze();
        }

        public string Name { get; }
        public Color Color { get; }
        public Brush Brush { get; }
    }
}

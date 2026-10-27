using System.Windows;
using System.Windows.Automation.Peers;
using PcChecker.Core.Accessibility;

namespace PcChecker.App.Controls;

/// <summary>
/// A Fluent symbol icon (the same as <c>ui:SymbolIcon</c>) that screen readers handle properly. WPF UI's icon puts
/// its font glyph in a text block, so Narrator would stop on it and read an unknown character. This icon hides the
/// glyph: an icon without a name is decoration and is skipped; an icon with <c>AutomationProperties.Name</c> or a
/// text tooltip carries meaning and is announced as an image with that name. Use it for every standalone icon in
/// XAML (<c>XamlAccessibilityTests</c> checks this).
/// </summary>
public class IconGlyph : Wpf.Ui.Controls.SymbolIcon
{
    protected override AutomationPeer OnCreateAutomationPeer() => new IconGlyphAutomationPeer(this);
}

/// <summary>Automation peer for <see cref="IconGlyph"/>: an image with a name, or nothing at all.</summary>
public sealed class IconGlyphAutomationPeer : FrameworkElementAutomationPeer
{
    public IconGlyphAutomationPeer(IconGlyph owner)
        : base(owner)
    {
    }

    protected override string GetClassNameCore() => "Icon";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

    // The glyph's text block is an implementation detail: never expose it.
    protected override List<AutomationPeer>? GetChildrenCore() => null;

    protected override string GetNameCore()
    {
        var name = base.GetNameCore();
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        return Owner is FrameworkElement { ToolTip: string tip } ? AccessibleText.ToName(tip) ?? string.Empty : string.Empty;
    }

    protected override bool IsControlElementCore() => HasMeaning();

    protected override bool IsContentElementCore() => HasMeaning();

    private bool HasMeaning() => !string.IsNullOrEmpty(GetNameCore());
}

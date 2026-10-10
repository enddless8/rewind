using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using ToolTip = System.Windows.Controls.ToolTip;

namespace Replay;

// Only settings info tips opt in; WPF continues to own popup DPI and screen bounds.
public static class InfoToolTipPlacement
{
    public static readonly DependencyProperty CenterAboveProperty = DependencyProperty.RegisterAttached(
        "CenterAbove", typeof(bool), typeof(InfoToolTipPlacement), new PropertyMetadata(false, Changed));

    public static bool GetCenterAbove(DependencyObject target) => (bool)target.GetValue(CenterAboveProperty);
    public static void SetCenterAbove(DependencyObject target, bool value) => target.SetValue(CenterAboveProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is ToolTip tip)
            tip.CustomPopupPlacementCallback = (bool)args.NewValue ? CenterAbove : null;
    }

    private static CustomPopupPlacement[] CenterAbove(Size popup, Size target, Point offset) =>
        // WPF already offsets the target origin; use target-relative coordinates here.
        [new(new Point((target.Width - popup.Width) / 2, -popup.Height), PopupPrimaryAxis.Horizontal)];
}

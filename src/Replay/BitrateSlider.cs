using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Replay;

// Integer UI input only; the existing BitrateBox/ReadSettings binding remains intact.
public sealed class BitrateSlider : Slider
{
    private bool dragging;
    private double grabOffset;
    private Track? Track => Template?.FindName("PART_Track", this) as Track;

    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) =>
        new Rect(RenderSize).Contains(parameters.HitPoint) ? new PointHitTestResult(this, parameters.HitPoint) : null;

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) { base.OnPreviewMouseDown(e); return; }
        if (!IsEnabled || Track is not { Thumb: { } thumb } track) return;
        Focus();
        var point = e.GetPosition(track);
        var center = thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2), track);
        var bounds = new Rect(thumb.TranslatePoint(new Point(), track), thumb.RenderSize);
        grabOffset = bounds.Contains(point) ? point.X - center.X : 0;
        dragging = CaptureMouse();
        MovePointer(point);
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (!dragging || Track is not { } track) return;
        MovePointer(e.GetPosition(track));
        e.Handled = true;
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !dragging) { base.OnPreviewMouseUp(e); return; }
        if (Track is { } track) MovePointer(e.GetPosition(track));
        dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        dragging = false;
        base.OnLostMouseCapture(e);
    }

    private void MovePointer(Point pointer)
    {
        if (Track is not { Thumb: { } thumb } track) return;
        var travel = track.ActualWidth - thumb.ActualWidth;
        if (travel <= 0) return;
        var fraction = Math.Clamp((pointer.X - grabOffset - thumb.ActualWidth / 2) / travel, 0, 1);
        if (IsDirectionReversed) fraction = 1 - fraction;
        SetCurrentValue(ValueProperty, Math.Round(Minimum + fraction * (Maximum - Minimum), MidpointRounding.AwayFromZero));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var next = e.Key switch
        {
            Key.Left or Key.Down => Value - 1,
            Key.Right or Key.Up => Value + 1,
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => double.NaN
        };
        if (double.IsNaN(next)) { base.OnPreviewKeyDown(e); return; }
        SetCurrentValue(ValueProperty, Math.Clamp(next, Minimum, Maximum));
        e.Handled = true;
    }
}

// Match the track's 6-DIP template margins and 12-DIP thumb; clamp the label inside it.
public sealed class BitrateValuePositionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 5 || values.Any(value => value is not double)) return new Thickness();
        var value = (double)values[0];
        var minimum = (double)values[1];
        var maximum = (double)values[2];
        var width = (double)values[3];
        var labelWidth = (double)values[4];
        if (maximum <= minimum || width <= 0) return new Thickness();
        var center = 12 + (value - minimum) / (maximum - minimum) * Math.Max(0, width - 24);
        return new Thickness(Math.Clamp(center - labelWidth / 2, 0, Math.Max(0, width - labelWidth)), 0, 0, 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => System.Windows.Data.Binding.DoNothing).ToArray();
}

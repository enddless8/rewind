using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Replay;

// Mouse-only magnetism: bindings and the existing integer keyboard handler stay exact.
public sealed class RewindDurationSlider : Slider
{
    private bool dragging;
    private double grabOffset;
    private Track? Track => Template?.FindName("PART_Track", this) as Track;

    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) =>
        new Rect(RenderSize).Contains(parameters.HitPoint) ? new PointHitTestResult(this, parameters.HitPoint) : null;

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) { base.OnPreviewMouseDown(e); return; }
        if (Track is not { Thumb: { } thumb } track || !IsEnabled) return;
        Focus();
        var point = e.GetPosition(track);
        var center = thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2), track);
        var thumbBounds = new Rect(thumb.TranslatePoint(new Point(), track), thumb.RenderSize);
        grabOffset = thumbBounds.Contains(point) ? point.X - center.X : 0;
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
        pointer.X -= grabOffset;
        var travel = track.ActualWidth - thumb.ActualWidth;
        if (travel <= 0) return;
        var fraction = Math.Clamp((pointer.X - thumb.ActualWidth / 2) / travel, 0, 1);
        if (IsDirectionReversed) fraction = 1 - fraction;
        var raw = Minimum + fraction * (Maximum - Minimum);
        // Absolute pointer coordinates let the thumb escape a magnet on a slow drag.
        SetCurrentValue(ValueProperty, SnapToLandmark(raw, travel));
    }

    private double SnapToLandmark(double raw, double travel)
    {
        var landmarks = RewindDurationPositionConverter.LandmarkPositions;
        for (var i = 0; i < landmarks.Count; i++)
        {
            var nearestGap = Math.Min(i > 0 ? landmarks[i] - landmarks[i - 1] : double.PositiveInfinity,
                i + 1 < landmarks.Count ? landmarks[i + 1] - landmarks[i] : double.PositiveInfinity);
            // Compact nonlinear intervals need a much narrower magnet than the 4-DIP
            // ceiling: keep only 2% of the nearest visual interval attractive.
            var radius = Math.Min(4, nearestGap * travel / (Maximum - Minimum) * .02);
            if (Math.Abs(raw - landmarks[i]) * travel / (Maximum - Minimum) <= radius)
            {
                raw = landmarks[i];
                break;
            }
        }
        return raw;
    }
}

// Label centers follow the same scale as the track. Narrow layouts stagger collisions.
public sealed class DurationLandmarkPanel : Panel
{
    private List<Rect> ArrangeLabels(double width)
    {
        var rectangles = new List<Rect>();
        var rowEnds = new List<double>();
        var rowHeight = Math.Ceiling(InternalChildren.Cast<UIElement>().Max(x => x.DesiredSize.Height)) + 2;
        foreach (FrameworkElement child in InternalChildren)
        {
            var position = RewindDurationPositionConverter.PositionFromSeconds(int.Parse(child.Tag.ToString()!));
            var center = 12 + Math.Max(0, width - 24) * position / 5;
            var left = Math.Clamp(center - child.DesiredSize.Width / 2, 0, Math.Max(0, width - child.DesiredSize.Width));
            var row = 0;
            while (row < rowEnds.Count && left < rowEnds[row] + 2) row++;
            if (row == rowEnds.Count) rowEnds.Add(0);
            rowEnds[row] = left + child.DesiredSize.Width;
            rectangles.Add(new Rect(left, row * rowHeight, child.DesiredSize.Width, Math.Ceiling(child.DesiredSize.Height)));
        }
        return rectangles;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (InternalChildren.Count == 0) return new Size();
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 273;
        return new Size(width, ArrangeLabels(width).Max(x => x.Bottom));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count == 0) return finalSize;
        var rectangles = ArrangeLabels(finalSize.Width);
        for (var i = 0; i < InternalChildren.Count; i++) InternalChildren[i].Arrange(rectangles[i]);
        return finalSize;
    }
}

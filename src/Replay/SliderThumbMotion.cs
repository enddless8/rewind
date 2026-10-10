using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;

namespace Replay;

// Presentation only. Observe handled pointer events too: nonlinear sliders capture
// the mouse themselves, while the native audio sliders capture their Thumb.
public static class SliderThumbMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SliderThumbMotion), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyPropertyKey IsPressedKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsPressed", typeof(bool), typeof(SliderThumbMotion), new PropertyMetadata(false));
    public static readonly DependencyProperty IsPressedProperty = IsPressedKey.DependencyProperty;
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(PointerState), typeof(SliderThumbMotion));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetIsPressed(DependencyObject target) => (bool)target.GetValue(IsPressedProperty);
    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Slider slider) return;
        if (slider.GetValue(StateProperty) is PointerState old) old.Detach();
        slider.ClearValue(StateProperty);
        if ((bool)e.NewValue) slider.SetValue(StateProperty, new PointerState(slider));
    }
    private sealed class PointerState
    {
        private readonly Slider slider;
        private Window? window;
        public PointerState(Slider slider)
        {
            this.slider = slider;
            slider.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Down), true);
            slider.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Up), true);
            slider.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(Lost), true);
            slider.IsEnabledChanged += AvailabilityChanged;
            slider.IsVisibleChanged += AvailabilityChanged;
            slider.Unloaded += Unloaded;
        }
        private void Down(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !slider.IsEnabled || !slider.IsVisible) return;
            if (slider.Template?.FindName("PART_Track", slider) is not Track { Thumb: not null } track) return;
            if (slider is not (RewindDurationSlider or BitrateSlider) && !new Rect(track.RenderSize).Contains(e.GetPosition(track))) return;
            Reset();
            window = Window.GetWindow(slider);
            if (window != null)
            {
                window.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Up), true);
                window.Deactivated += Deactivated;
            }
            slider.SetValue(IsPressedKey, true);
        }
        private void Up(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) Reset(); }
        private void Lost(object sender, MouseEventArgs e) { if (!slider.IsMouseCaptureWithin) Reset(); }
        private void AvailabilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (slider.IsEnabled && slider.IsVisible) return;
            Reset();
            if (slider.IsMouseCaptureWithin) Mouse.Capture(null);
        }
        private void Unloaded(object sender, RoutedEventArgs e) => Reset();
        private void Deactivated(object? sender, EventArgs e) => Reset();
        private void Reset()
        {
            slider.SetValue(IsPressedKey, false);
            if (window == null) return;
            window.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Up));
            window.Deactivated -= Deactivated;
            window = null;
        }
        public void Detach()
        {
            Reset();
            slider.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Down));
            slider.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(Up));
            slider.RemoveHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(Lost));
            slider.IsEnabledChanged -= AvailabilityChanged;
            slider.IsVisibleChanged -= AvailabilityChanged;
            slider.Unloaded -= Unloaded;
        }
    }
}

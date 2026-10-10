using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Replay;

internal partial class OverlayWindow : Window
{
    public nint Handle { get; private set; }
    public OverlayNotification Notification { get; private set; } = null!;
    public System.Windows.Media.Imaging.BitmapSource? Thumbnail { get; private set; }
    private OverlayBounds placement;
    private int moveRevision;
    public bool IsMoving { get; private set; }
    public event Action? PlacementSettled;
    private static readonly DependencyProperty PlacementTopProperty = DependencyProperty.Register("PlacementTop", typeof(double), typeof(OverlayWindow),
        new PropertyMetadata(0d, (d, e) => ((OverlayWindow)d).MoveNative((double)e.NewValue)));
    public OverlayWindow(OverlayNotification notification)
    {
        InitializeComponent();
        ApplyNotification(notification);
        Closed += (_, _) =>
        {
            moveRevision++; IsMoving = false;
            SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            BeginAnimation(PlacementTopProperty, null);
            BeginAnimation(OpacityProperty, null);
        };
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(Handle).AddHook(WindowProc);
            var style = GetWindowLongPtr(Handle, -20).ToInt64();
            SetWindowLongPtr(Handle, -20, (nint)((style | 0x080000A0 /* NOACTIVATE | TOOLWINDOW | TRANSPARENT */) & ~0x40000));
            // Fail closed: an unprotected notification is never shown over captured content.
            if (!SetWindowDisplayAffinity(Handle, 0x11) || !GetWindowDisplayAffinity(Handle, out var affinity) || affinity != 0x11)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось исключить уведомление Rewind из захвата.");
        };
    }

    public void ApplyNotification(OverlayNotification notification)
    {
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        SpinnerRotation.Angle = 0;
        Notification = notification;
        Card.Height = notification.Height;
        LargeFields.Visibility = notification.IsMicro ? Visibility.Collapsed : Visibility.Visible;
        MicroFields.Visibility = notification.IsMicro ? Visibility.Visible : Visibility.Collapsed;
        MicroBody.Text = notification.Body;
        MicroBody.Margin = new Thickness(notification.Kind == OverlayNotificationKind.CompressionStarted ? 1 : 0, 14, 0, 0);
        CompletionCheck.Visibility = notification.Kind == OverlayNotificationKind.CompressionCompleted ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.Visibility = notification.Kind == OverlayNotificationKind.RewindSaving ? Visibility.Visible : Visibility.Collapsed;
        if (notification.Kind == OverlayNotificationKind.RewindSaving)
            SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        Thumbnail = null;
        ThumbnailBrush.ImageSource = null;
        Heading.Text = notification.Title;
        Metadata.Text = notification.Metadata;
        MetadataBox.Visibility = notification.Metadata.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var media = notification.HasThumbnail;
        var mic = notification.Kind is OverlayNotificationKind.MicrophoneEnabled or OverlayNotificationKind.MicrophoneMuted;
        LargeFields.Margin = new Thickness(29, 0, notification.Kind == OverlayNotificationKind.RewindSaving ? 33 : 18, 0);
        MediaColumn.Width = new GridLength(media ? 171 : mic ? 100 : notification.Kind == OverlayNotificationKind.RewindSaving ? 93 : 0);
        MediaGap.Width = new GridLength(media || notification.Kind == OverlayNotificationKind.RewindSaving ? 20 : mic ? 16 : 0);
        ThumbnailFrame.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneIcon.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        Copy.Margin = new Thickness(0, media ? 10 : notification.Kind == OverlayNotificationKind.RewindEnabled ? 16 : 31, 0, 0);
        BodyBox.Margin = new Thickness(0, media ? 14 : 0, 0, 0);
        Body.Inlines.Clear();
        BodyBox.Visibility = Visibility.Visible;
        if (!media)
        {
            BodyBox.Margin = new Thickness(0);
        }
        if (mic)
        {
            MicrophoneIcon.Visibility = Visibility.Visible;
            MicrophoneIcon.Source = (ImageSource)FindResource(notification.Kind == OverlayNotificationKind.MicrophoneMuted
                ? "MainFigmaMicrophoneMutedIcon" : "MainFigmaMicrophoneIcon");
        }
        if (notification.Body.Length == 0)
        {
            Copy.Margin = new Thickness(0, 48, 0, 0);
            BodyBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            BodyBox.Height = media && notification.Metadata.Length == 0 || notification.Kind == OverlayNotificationKind.RewindEnabled ? 62 : 31;
            var emphasis = notification.Emphasis;
            var at = emphasis == null ? -1 : notification.Body.IndexOf(emphasis, StringComparison.Ordinal);
            if (at < 0) Body.Text = notification.Body;
            else
            {
                Body.Inlines.Add(new Run(notification.Body[..at]));
                Body.Inlines.Add(new Run(emphasis) { FontWeight = FontWeights.SemiBold });
                Body.Inlines.Add(new Run(notification.Body[(at + emphasis!.Length)..]));
            }
        }
    }

    public void ShowAt(OverlayBounds bounds)
    {
        Width = OverlayPlacement.CardWidth * bounds.ContentScale;
        Height = Notification.Height * bounds.ContentScale;
        new WindowInteropHelper(this).EnsureHandle();
        Reposition(bounds, false);
        Opacity = 0;
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    public void Reposition(OverlayBounds bounds, bool animate = true)
    {
        var revision = ++moveRevision;
        var from = (double)GetValue(PlacementTopProperty);
        placement = bounds;
        IsMoving = false;
        BeginAnimation(PlacementTopProperty, null);
        SetValue(PlacementTopProperty, (double)bounds.Top);
        if (animate && from != bounds.Top)
        {
            IsMoving = true;
            var movement = new DoubleAnimation(from, bounds.Top, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            movement.Completed += (_, _) =>
            {
                if (revision != moveRevision) return;
                IsMoving = false; BeginAnimation(PlacementTopProperty, null);
                PlacementSettled?.Invoke();
            };
            BeginAnimation(PlacementTopProperty, movement);
        }
        else MoveNative(bounds.Top);
    }
    private void MoveNative(double top)
    {
        if (Handle != 0) SetWindowPos(Handle, (nint)(-1), placement.Left, (int)Math.Round(top), placement.Width, placement.Height, 0x0010 /* NOACTIVATE */);
    }

    public void Revive()
    { BeginAnimation(OpacityProperty, null); Opacity = 1; }

    public void FadeOut(Action finished)
    {
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        animation.Completed += (_, _) => finished();
        BeginAnimation(OpacityProperty, animation);
    }

    public void SetThumbnail(System.Windows.Media.Imaging.BitmapSource image)
    { Thumbnail = image; ThumbnailBrush.ImageSource = image; }

    private static nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x21 /* MOUSEACTIVATE */) { handled = true; return 3 /* MA_NOACTIVATE */; }
        if (msg == 0x84 /* NCHITTEST */) { handled = true; return -1 /* HTTRANSPARENT */; }
        return 0;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}

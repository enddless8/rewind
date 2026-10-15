using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Replay;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;

internal static class ServiceInfoChecks
{
    internal static async Task Run(MainWindow window, string root, bool restart)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
        T Find<T>(string name) => (T)window.FindName(name);
        var toggle = Find<CheckBox>("ShowServiceInfoCheck");
        var panel = Find<StackPanel>("FeedbackPanel");
        var main = Find<Grid>("MainView");
        var clip = Find<TextBlock>("LastClipText");
        async Task Layout() { await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle); }
        async Task Toggle(bool value)
        {
            toggle.IsChecked = value;
            Program.Call(window, "ShowServiceInfo_Click", toggle, new RoutedEventArgs());
            await Layout();
        }
        void Render(string name)
        {
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(root, name + ".png")); encoder.Save(file);
        }
        Check(toggle.IsChecked == restart, "Persisted switch restored on process startup");
        Check(!new Settings().ShowServiceInfo && !Settings.FromJson("{}").ShowServiceInfo, "Fresh and old configurations default to false");
        if (!restart)
        {
            var file = Path.Combine(Settings.DataDirectory, "settings.json");
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            legacy.AsObject().Remove(nameof(Settings.ShowServiceInfo)); File.WriteAllText(file, legacy.ToJsonString());
            Check(!Settings.Load().ShowServiceInfo, "Old settings.json loads with false");
        }
        clip.Text = "Сохранено 10 с · 2026-10-16-01-40-38.mkv";
        await Toggle(false);
        Check(panel.Visibility == Visibility.Collapsed && Grid.GetRowSpan(main) == 2, "Disabled footer collapses and main view fills freed space");
        Render("main-hidden");
        var overlays = Program.Field(window, "overlays");
        var registrations = new[] { "saveRegistration", "toggleRegistration", "recordingRegistration", "screenshotRegistration" }.Select(n => Program.Field(window, n)).ToArray();
        await Toggle(true);
        Check(panel.IsVisible && panel.ActualHeight > 0 && Grid.GetRowSpan(main) == 1, "Switch shows existing footer immediately");
        Check(Settings.Load().ShowServiceInfo, "Enabled value saved to disk");
        Check(ReferenceEquals(overlays, Program.Field(window, "overlays")), "Notification service preserved");
        Check(registrations.SequenceEqual(new[] { "saveRegistration", "toggleRegistration", "recordingRegistration", "screenshotRegistration" }.Select(n => Program.Field(window, n))), "Hotkey registrations unchanged by switch");
        Render("main-shown");
        await Toggle(false);
        Check(!Settings.Load().ShowServiceInfo && panel.Visibility == Visibility.Collapsed, "Disabled value saved and footer hidden immediately");
        Check(clip.Text.Contains("Сохранено 10 с"), "Footer contents retained while hidden");
        var service = (OverlayNotificationService)overlays!;
        var published = Path.Combine(root, "notification-fixture.mkv");
        var media = await MediaTools.Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "color=s=64x64:r=1", "-t", "1", "-c:v", "libx264", published]);
        Check(media.Code == 0, "Notification media fixture created");
        var operation = Guid.NewGuid();
        service.BeginRewindSave(operation);
        await Layout();
        service.CompleteRewindSave(operation, new SavedClip(published, 10, []));
        await Layout();
        Check(service.Windows.Any(w => w.IsVisible && w.Notification.Kind == OverlayNotificationKind.RewindSaved), "Save notification displays with footer disabled");
        var job = Guid.NewGuid().ToString();
        service.BeginCompression(Guid.NewGuid(), job);
        await Layout(); service.FinishCompression(job, true); await Layout();
        Check(service.Windows.Any(w => w.IsVisible && w.Notification.Kind == OverlayNotificationKind.CompressionCompleted), "Export notification displays with footer disabled");
        var notices = new List<string>();
        void Notice(string stage, object? details) { if (stage == "notification") notices.Add(JsonSerializer.Serialize(details)); }
        HotkeyDiagnostics.Observed += Notice;
        try { Program.Call(window, "Notify", "Ошибка проверки", "Тест уведомления", true); }
        finally { HotkeyDiagnostics.Observed -= Notice; }
        Check(notices.Any(n => n.Contains("\"warning\":true")), "Error balloon path runs with footer disabled");
        await Toggle(true);
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        Check(Find<TextBlock>("VersionText").Text == "Rewind v" + version, "Version matches assembly metadata");
        Program.Call(window, "Settings_Click", window, new RoutedEventArgs());
        Find<RadioButton>("AdvancedNav").IsChecked = true;
        await Layout(); Render("advanced");
        Check(toggle.IsVisible && Find<TextBlock>("VersionText").IsVisible, "Advanced section shows switch and version");
        File.WriteAllText(Path.Combine(root, restart ? "service-info-restart.json" : "service-info.json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

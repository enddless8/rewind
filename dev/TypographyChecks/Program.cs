using System.IO;
using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Replay;

internal static class Program
{
    // Render the application's XAML without starting capture, tray, hotkeys,
    // settings persistence or compression queues. This is layout QA, not a
    // replacement for interactive native-window testing.
    [STAThread]
    static int Main(string[] args)
    {
        var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        try
        {
            var app = new App(); app.InitializeComponent();
            var assembly = typeof(App).Assembly;
            var resources = new List<string>();
            var embeddedFonts = new List<object>();
            using (var reader = new ResourceReader(assembly.GetManifestResourceStream("Rewind.g.resources")!))
                foreach (System.Collections.DictionaryEntry item in reader)
                {
                    var key = (string)item.Key; resources.Add(key);
                    if ((key.EndsWith(".ttf") || key.EndsWith(".otf")) && item.Value is Stream stream)
                        embeddedFonts.Add(new { Path = key, Bytes = stream.Length, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant() });
                }
            File.WriteAllText(Path.Combine(output, "embedded-resources.json"), JsonSerializer.Serialize(resources, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "embedded-fonts.json"), JsonSerializer.Serialize(embeddedFonts, new JsonSerializerOptions { WriteIndented = true }));
            var evidence = new List<object>();
            var layout = new List<object>();
            if (args[0] != "before")
            {
                if (resources.Any(x => x.Contains("coolvetica", StringComparison.OrdinalIgnoreCase))) throw new Exception("Obsolete font embedded");
                var family = (FontFamily)app.Resources["RewindticaFont"];
                foreach (var weight in new[] { FontWeights.Light, FontWeights.Medium, FontWeights.Bold })
                {
                    var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
                    if (!typeface.TryGetGlyphTypeface(out var glyph) || glyph.Weight != weight || !glyph.FontUri.ToString().Contains("rewindtica", StringComparison.OrdinalIgnoreCase)) throw new Exception($"Wrong face: {weight}");
                    const string text = "Настройки Сохранить Rewind Начать запись Сделать скриншот 180 сек Захват видео Аудио Вывод Горячие клавиши Дополнительно";
                    if (text.Any(c => !glyph.CharacterToGlyphMap.ContainsKey(c))) throw new Exception("Missing Cyrillic glyph");
                    evidence.Add(new { Weight = weight.ToOpenTypeWeight(), Uri = glyph.FontUri.ToString(), glyph.StyleSimulations });
                    if (glyph.StyleSimulations != StyleSimulations.None) throw new Exception("Synthetic face");
                }
            }
            var installed = Fonts.SystemFontFamilies.Any(f => f.FamilyNames.Values.Any(n => n.Contains("Rewindtica", StringComparison.OrdinalIgnoreCase)));
            if (args[0] == "fonts")
            {
                File.WriteAllText(Path.Combine(output, "font-checks.json"), JsonSerializer.Serialize(new { Passed = true, InstalledGlobally = installed, Faces = evidence }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            var xml = XDocument.Load("src/Replay/MainWindow.xaml");
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            xml.Root!.Attribute(x + "Class")!.Remove();
            xml.Root!.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:Replay;assembly=Rewind");
            foreach (var element in xml.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:Replay"))
                element.Name = XName.Get(element.Name.LocalName, "clr-namespace:Replay;assembly=Rewind");
            var events = typeof(Window).Assembly.GetTypes().Concat(assembly.GetTypes())
                .SelectMany(t => t.GetEvents()).Select(e => e.Name).ToHashSet();
            foreach (var attribute in xml.Descendants().Attributes().ToArray())
                if (events.Contains(attribute.Name.LocalName) && attribute.Name.Namespace == XNamespace.None) attribute.Remove();
            var window = (Window)XamlReader.Parse(xml.ToString());
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            var root = new Border { Background = window.Background, Child = content };
            window.Content = root;
            T Find<T>(string name) where T : class => (T)window.FindName(name);
            void Render(string name, double scale, double width = 820, double height = 643)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => {}, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{name}-{scale * 100:0}.png")); encoder.Save(file);
                foreach (var block in Descendants(root).OfType<TextBlock>().Where(b => b.FontFamily.Source.Contains("Rewindtica", StringComparison.OrdinalIgnoreCase) && Visible(b, root)))
                {
                    var measured = block.Text.Split('\n').Select(line => new FormattedText(line, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                        new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.White, scale).WidthIncludingTrailingWhitespace).DefaultIfEmpty().Max();
                    layout.Add(new { Screen = name, Scale = scale, block.Text, block.FontSize, Weight = block.FontWeight.ToOpenTypeWeight(), Available = block.ActualWidth, Required = measured });
                    if (args[0] != "before" && measured > block.ActualWidth + 1) throw new Exception($"Clipped text on {name}: {block.Text} requires {measured}, has {block.ActualWidth}");
                }
                if (args[0] != "before" && Find<Grid>("MainView").Visibility == Visibility.Visible)
                {
                    Rect Ink(TextBlock b)
                    {
                        var text = new FormattedText(b.Text, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                            new Typeface(b.FontFamily,b.FontStyle,b.FontWeight,b.FontStretch),b.FontSize,Brushes.White,scale);
                        return b.TransformToAncestor(root).TransformBounds(new Rect((b.ActualWidth-text.Width)/2,0,text.Width,b.FontSize));
                    }
                    var recording = Ink(Find<TextBlock>("RecordingLabel"));
                    var shot = Ink(Descendants(root).OfType<TextBlock>().Single(b => b.Text == "Сделать скриншот"));
                    if (recording.Right + 4 > shot.Left) throw new Exception("Recording and screenshot labels collide");
                    layout.Add(new { Screen = name, Scale = scale, Gap = shot.Left - recording.Right });
                }
            }
            foreach (var kind in Enum.GetValues<OverlayNotificationKind>())
            {
                var title = kind == OverlayNotificationKind.RewindSaved ? "Rewinded!" : kind == OverlayNotificationKind.RewindEnabled ? "Rewind активен" : kind == OverlayNotificationKind.RewindDisabled ? "Rewind отключён" : "Rewind";
                var body = kind switch {
                    OverlayNotificationKind.RewindSaved => "Сохранены\nпоследние 30 сек",
                    OverlayNotificationKind.RecordingStarted => "Запись началась",
                    OverlayNotificationKind.RecordingSaved => "Запись сохранена",
                    OverlayNotificationKind.ScreenshotSaved => "Скриншот сохранён",
                    OverlayNotificationKind.RewindEnabled => "Нажмите Ctrl+Alt+F10,\nчтобы сохранить Rewind",
                    OverlayNotificationKind.RewindDisabled => "",
                    OverlayNotificationKind.MicrophoneEnabled => "Микрофон включен",
                    OverlayNotificationKind.MicrophoneMuted => "Микрофон отключён",
                    OverlayNotificationKind.RewindSaving => "Сохранение Rewind…",
                    OverlayNotificationKind.CompressionStarted => "Сжатие видео...",
                    _ => "Готово" };
                var notification = new OverlayNotification(kind, title, body,
                    kind == OverlayNotificationKind.ScreenshotSaved ? "12 МБ" : kind == OverlayNotificationKind.RecordingSaved ? "01:23 · 50 МБ" : "");
                var overlay = new OverlayWindow(notification);
                var visual = (FrameworkElement)overlay.Content;
                visual.Measure(new Size(496, notification.Height)); visual.Arrange(new Rect(0,0,496,notification.Height)); visual.UpdateLayout();
                foreach (var scale in new[] { 1d, 1.5, 2d }) Save(visual, 496, notification.Height, scale, output, "overlay-" + kind);
                foreach (var block in Descendants(visual).OfType<TextBlock>().Where(b => b.FontFamily.Source.Contains("Rewindtica") && Visible(b)))
                {
                    var text = new FormattedText(block.Text, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.White, 1);
                    layout.Add(new { Screen = "overlay-" + kind, block.Text, Available = block.ActualWidth, Required = text.Width });
                    if (args[0] != "before" && text.Width > block.ActualWidth + 1) throw new Exception("Clipped overlay: " + block.Text);
                }
                if (notification.IsMicro)
                {
                    var heading = Descendants(visual).OfType<TextBlock>().Single(b => b.FontFamily.Source.Contains("Rewindtica") && Visible(b));
                    var bodyBlock = (TextBlock)overlay.FindName("MicroBody");
                    double TextWidth(TextBlock b) => new FormattedText(b.Text,CultureInfo.GetCultureInfo("ru-RU"),FlowDirection.LeftToRight,
                        new Typeface(b.FontFamily,b.FontStyle,b.FontWeight,b.FontStretch),b.FontSize,Brushes.White,1).Width;
                    var titleRight = heading.TransformToAncestor(visual).Transform(new Point(TextWidth(heading),0)).X;
                    var bodyLeft = bodyBlock.TransformToAncestor(visual).Transform(new Point()).X;
                    if (bodyLeft-titleRight < 6) throw new Exception("Compact overlay title/body collide");
                    if (kind == OverlayNotificationKind.CompressionCompleted)
                    {
                        var check = (FrameworkElement)overlay.FindName("CompletionCheck");
                        var checkLeft = check.TransformToAncestor(visual).Transform(new Point()).X;
                        if (checkLeft-bodyLeft-TextWidth(bodyBlock) < 6) throw new Exception("Compact completion text/check collide");
                    }
                    layout.Add(new { Screen = "compact-spacing-" + kind, Gap = bodyLeft-titleRight });
                }
            }
            var tooltip = new ToolTip { Style = (Style)app.Resources["SettingsInfoToolTip"], Content = "Для режима захвата рабочего стола" };
            tooltip.Measure(new Size(220,200)); tooltip.Arrange(new Rect(tooltip.DesiredSize)); tooltip.UpdateLayout();
            foreach (var scale in new[] {1d,1.5,2d}) Save(tooltip, tooltip.ActualWidth, tooltip.ActualHeight, scale, output, "tooltip");
            foreach (var scale in new[] { 1d, 1.5, 2d })
            {
                Find<Grid>("MainView").Visibility = Visibility.Visible; Find<Grid>("SettingsView").Visibility = Visibility.Collapsed;
                foreach (var on in new[] { false, true })
                {
                    Find<Button>("SaveButton").IsEnabled = on;
                    Find<System.Windows.Shapes.Ellipse>("BufferStateDot").Fill = on ? Brushes.LawnGreen : (Brush)app.Resources["MainRewindInactive"];
                    Find<Border>("RewindSelector").Visibility = Visibility.Collapsed; Render(on ? "main-on" : "main-off", scale);
                    Find<Border>("RewindSelector").Visibility = Visibility.Visible; Render(on ? "menu-on" : "menu-off", scale);
                }
                Find<Border>("RewindSelector").Visibility = Visibility.Collapsed;
                Find<TextBlock>("RecordingLabel").Text = "Остановить запись"; Render("recording", scale);
                Find<TextBlock>("RecordingLabel").Text = "Начать запись";
                Find<Grid>("MainView").Visibility = Visibility.Collapsed; Find<Grid>("SettingsView").Visibility = Visibility.Visible;
                foreach (var page in new[] { "Capture", "Audio", "Output", "Hotkeys", "Advanced" })
                {
                    foreach (var other in new[] { "Capture", "Audio", "Output", "Hotkeys", "Advanced" }) Find<RadioButton>(other + "Nav").IsChecked = other == page;
                    Render("settings-" + page.ToLowerInvariant(), scale);
                    Render("settings-" + page.ToLowerInvariant() + "-minimum", scale, 640, 529);
                }
                Find<Grid>("MainView").Visibility = Visibility.Visible; Find<Grid>("SettingsView").Visibility = Visibility.Collapsed;
                Render("main-minimum", scale, 640, 529);
            }
            File.WriteAllText(Path.Combine(output, "layout-checks.json"), JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "font-checks.json"), JsonSerializer.Serialize(new { Passed = true, InstalledGlobally = installed, Faces = evidence, Note = "WPF layout rasterization at 96/144/192 DPI; native monitor scaling not changed" }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    static bool Visible(DependencyObject element, DependencyObject? stop = null)
    {
        for (DependencyObject? node = element; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement ui && node is not Window && ui.Visibility != Visibility.Visible) return false;
            if (node == stop) break;
        }
        return true;
    }
    static void Save(Visual visual, double width, double height, double scale, string output, string name)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(width*scale)), Math.Max(1,(int)Math.Ceiling(height*scale)), 96*scale,96*scale,PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output,$"{name}-{scale*100:0}.png")); encoder.Save(file);
    }
}

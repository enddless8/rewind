using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Replay;
internal static class WindowCaptureTest
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    public static async Task<int> Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        var diagnostics = new List<object>();
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        var target = new Window { Title = "Rewind — проверка рамки окна", Width = 680, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 30, 65)),
            Content = new TextBlock { Text = "REPLAY / WINDOW CAPTURE", Foreground = System.Windows.Media.Brushes.White, FontSize = 26, Margin = new Thickness(35) } };
        try
        {
            target.Show(); await Task.Delay(300);
            var handle = new WindowInteropHelper(target).Handle;
            ShowWindow(handle, 5); target.Topmost = true; target.Activate();
            animation.Tick += (_, _) => ((TextBlock)target.Content).Text = $"REPLAY / WINDOW CAPTURE\n{DateTime.Now:HH:mm:ss.fff}";
            animation.Start(); await Task.Delay(300);
            var game = new GameWindow(handle, Environment.ProcessId, "Replay-Window-Test", target.Title);
            var display = Native.Displays().First(d => d.Primary);
            var (adapter, output) = DesktopCapture.Find(display);
            GetWindowRect(handle, out var rect);
            diagnostics.Add(new { WindowHandle = handle.ToString(), rect.Left, rect.Top, rect.Right, rect.Bottom });
            var counts = new List<int>();
            foreach (var hidden in new[] { false, true })
            {
                var settings = new Settings { CaptureMode = "Game", AudioMode = "Game", MicrophoneId = "off", Discord = false,
                    HideDisplayBorder = hidden, Height = 720, Fps = 30, BufferSeconds = 15,
                    BufferDirectory = Path.Combine(directory, "buffer"), OutputDirectory = Path.Combine(directory, "clips") };
                await using var recording = new Recorder(settings, game, display);
                await recording.Start(); await Task.Delay(2500);
                var screenshot = Path.Combine(directory, hidden ? "border-hidden.png" : "border-visible.png");
                var shot = await MediaTools.Run("ffmpeg", ["-v", "error", "-init_hw_device", $"d3d11va=check:{adapter}", "-filter_hw_device", "check",
                    "-filter_complex", $"ddagrab=output_idx={output}:draw_mouse=0,hwdownload,format=bgra[v]", "-map", "[v]", "-frames:v", "1", "-y", screenshot]);
                Check(shot.Code == 0, "Desktop screenshot taken while window capture is active");
                var image = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(screenshot)), PixelFormats.Bgra32, null, 0);
                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                int yellow = 0;
                for (var y = Math.Max(0, rect.Top - 8); y < Math.Min(image.PixelHeight, rect.Bottom + 8); y++)
                    for (var x = Math.Max(0, rect.Left - 8); x < Math.Min(image.PixelWidth, rect.Right + 8); x++)
                    {
                        var p = (y * image.PixelWidth + x) * 4;
                        if (pixels[p + 2] > 200 && pixels[p + 1] > 140 && pixels[p] < 100) yellow++;
                    }
                counts.Add(yellow);
                var clip = await recording.Save();
                var decoded = await MediaTools.Run("ffmpeg", ["-v", "error", "-i", clip.Path, "-map", "0", "-f", "null", "-"]);
                Check(decoded.Code == 0, "Window clip and process audio decode");
                Check(recording.Running, "Window recording remains active after saving");
                diagnostics.Add(new { HideBorder = hidden, YellowPixels = yellow, recording.CaptureBackend, clip.Path, clip.Warnings });
                await recording.Stop(); await Task.Delay(350);
            }
            Check(counts[0] > 500 && counts[1] < counts[0] / 10, $"Yellow border disappears around the actual window: {counts[0]} -> {counts[1]} pixels");
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = true, checks, diagnostics }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = false, checks, diagnostics, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally { animation.Stop(); target.Close(); }
    }
}

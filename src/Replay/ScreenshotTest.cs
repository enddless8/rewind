using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace Replay;
internal static class ScreenshotTest
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    private static readonly BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static HashSet<int> FfmpegIds()
    {
        HashSet<int> result = [];
        foreach (var p in Process.GetProcessesByName("ffmpeg")) using (p) result.Add(p.Id);
        return result;
    }
    private static (int Width, int Height, byte[] Pixels) Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var png = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(png.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4]; frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        return (frame.PixelWidth, frame.PixelHeight, pixels);
    }
    private static bool HasBackground(byte[] pixels, Color color)
    {
        int same = 0;
        for (int p = 0; p < pixels.Length; p += 4)
            if (pixels[p] == color.B && pixels[p + 1] == color.G && pixels[p + 2] == color.R) same++;
        return same > pixels.Length / 4 * .7;
    }
    public static async Task<int> Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        List<string> checks = []; List<object> captures = [];
        void Check(bool good, string name) { if (!good) throw new Exception(name); checks.Add(name); }
        var config = Path.Combine(Settings.DataDirectory, "settings.json");
        var configBytes = File.Exists(config) ? File.ReadAllBytes(config) : null;
        var processes = FfmpegIds();
        Window? target = null;
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        try
        {
            Check(new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace > 300L * 1024 * 1024, "Enough disk space exists before native screenshot/capture tests");
            var display = Native.Displays().First(d => d.Primary);
            var color = Color.FromRgb(20, 80, 160);
            target = new Window { Title = "Проверка скриншотов Rewind", Width = 720, Height = 450, Topmost = true,
                Background = new SolidColorBrush(color), Content = new TextBlock { Text = "Проверка свежего кадра", FontSize = 32, Foreground = Brushes.White } };
            target.Show(); target.Activate(); await Task.Delay(200);
            animation.Tick += (_, _) => ((TextBlock)target.Content).Text = $"Проверка свежего кадра\n{DateTime.Now:HH:mm:ss.fff}";
            animation.Start();
            var game = new GameWindow(new WindowInteropHelper(target).Handle, Environment.ProcessId, "ScreenshotFixture", target.Title);
            GetClientRect(game.Handle, out var client);
            var settings = new Settings { CaptureMode = "Game", AudioMode = "Game", MicrophoneId = "default", Discord = false,
                HideDisplayBorder = true, Height = 720, Fps = 30, BufferSeconds = 20, Codec = "H264", GroupByApplication = true,
                OutputDirectory = Path.Combine(directory, "изображения"), BufferDirectory = Path.Combine(directory, "буфер") };
            settings.GameNames[game.Exe] = "Пример: приложение / PNG";
            var name = ClipFiles.ApplicationName(settings, game);
            var request = new ScreenshotRequest("Game", true, 30, game, display, settings.OutputDirectory, true, name);
            var graph = CaptureGraph.Build("Game", true, 30, game, display);
            var args = ScreenshotCapture.Arguments(graph, "fixture.partial");
            Check(!graph.Graph.Contains("width=") && !graph.Graph.Contains("height=") && graph.Graph.Contains("hwdownload,format=bgra"), "PNG graph downloads native WGC pixels without recording scaling/padding");
            Check(args.Contains("png") && args.Contains("-an") && args.Contains("1") && !args.Any(x => x.Contains("nvenc") || x.Contains("wasapi") || x.Contains("segment")), "One-shot graph encodes a single PNG with no audio, NVENC or Rewind session");
            Check(CaptureSource.ResolveGame(game, game with { Exe = "Other" }) == game && CaptureSource.ResolveGame(null, game) == game, "Screenshot and Rewind share explicit and last-active source precedence");
            var first = await ScreenshotCapture.Take(request);
            var firstImage = Decode(first.Path);
            Check(firstImage.Width == client.Right && firstImage.Height == client.Bottom && first.Width == client.Right && first.Height == client.Bottom,
                $"Rewind OFF PNG uses native selected-window client dimensions {client.Right}x{client.Bottom}");
            Check(HasBackground(firstImage.Pixels, color), "Decoded real PNG contains the configured application window rather than Rewind/desktop");
            Check(Path.GetDirectoryName(first.Path) == Path.Combine(settings.OutputDirectory, ClipFiles.SafeName(name)), "Application grouping reuses exact clip alias sanitization and output directory");
            Check(!Directory.Exists(settings.BufferDirectory), "OFF screenshot creates no Rewind buffer directory");
            Check(FfmpegIds().IsSubsetOf(processes), "OFF one-shot leaves no orphan FFmpeg process");
            var beforeBytes = File.ReadAllBytes(first.Path);
            color = Color.FromRgb(160, 30, 65); target.Background = new SolidColorBrush(color);
            await Dispatcher.Yield(DispatcherPriority.Render); await Task.Delay(120);
            var second = await ScreenshotCapture.Take(request with { GroupByApplication = false });
            Check(Path.GetDirectoryName(second.Path) == settings.OutputDirectory && HasBackground(Decode(second.Path).Pixels, color), "Grouping OFF publishes a fresh current frame directly to OutputDirectory");
            Check(first.Path != second.Path && beforeBytes.SequenceEqual(File.ReadAllBytes(first.Path)), "Timestamp/random naming never overwrites a previous PNG");
            Check(ClipFiles.SafeName("CON") == "Game-CON" && ClipFiles.SafeName("../") == ".._" && ClipFiles.SafeName("") == "Unknown", "Image folders preserve existing reserved/invalid clip-name rules");
            bool noOverwrite = false;
            var duplicate = Path.Combine(directory, "duplicate.partial"); File.WriteAllBytes(duplicate, [1, 2, 3]);
            try { await ClipFiles.PublishFile(duplicate, first.Path); } catch (IOException) { noOverwrite = true; }
            Check(noOverwrite && beforeBytes.SequenceEqual(File.ReadAllBytes(first.Path)) && File.Exists(duplicate), "Generic atomic publisher rejects a filename collision without modifying either file");
            File.Delete(duplicate);
            var mute = new MicrophoneMuteControl(); mute.SetMuted(true);
            // Concurrent capture of the same window/monitor, covering both production APIs and border settings.
            foreach (var mode in new[] { "Game", "Desktop" })
                foreach (var hidden in new[] { true, false })
                {
                    settings.CaptureMode = mode; settings.AudioMode = mode == "Game" ? "Game" : "System"; settings.HideDisplayBorder = hidden;
                    await using var recorder = new Recorder(settings, mode == "Game" ? game : null, display, mute);
                    await recorder.Start(); await Task.Delay(2200);
                    var producer = (Process)typeof(Recorder).GetField("video", Instance)!.GetValue(recorder)!;
                    var producerId = producer.Id; var session = recorder.SessionDirectory;
                    var tracks = ((List<AudioTrack>)typeof(Recorder).GetField("tracks", Instance)!.GetValue(recorder)!).ToArray();
                    var mic = recorder.Microphone;
                    var beforeSegments = Directory.GetFiles(session, "part-*.mkv", SearchOption.AllDirectories).ToDictionary(p => p, p => new FileInfo(p).Length);
                    var stopwatch = Stopwatch.StartNew();
                    var shot = await ScreenshotCapture.Take(request with { Mode = mode, HideBorder = hidden, Game = mode == "Game" ? game : null, ApplicationName = mode == "Game" ? name : null });
                    stopwatch.Stop(); var png = Decode(shot.Path);
                    captures.Add(new { mode, hidden, shot.Path, shot.Width, shot.Height, shot.CaptureBackend, Milliseconds = stopwatch.ElapsedMilliseconds });
                    Check(recorder.Running && producer.Id == producerId && recorder.SessionDirectory == session &&
                        beforeSegments.All(p => File.Exists(p.Key) && new FileInfo(p.Key).Length >= p.Value), "Same-source screenshot coexists without restarting or clearing Rewind: " + mode + "/" + hidden);
                    Check(((List<AudioTrack>)typeof(Recorder).GetField("tracks", Instance)!.GetValue(recorder)!).SequenceEqual(tracks) && ReferenceEquals(mic, recorder.Microphone) && mic?.IsMuted == true && mute.Muted,
                        "Screenshot owns no microphone/audio and preserves current runtime mute: " + mode + "/" + hidden);
                    Check(FfmpegIds().IsSubsetOf(processes.Append(producerId).ToHashSet()), "Temporary capture process is reaped while the original Rewind producer remains: " + mode + "/" + hidden);
                    if (mode == "Game") Check(png.Width == client.Right && png.Height == client.Bottom && HasBackground(png.Pixels, color), "Concurrent WGC PNG preserves selected window native size and content: " + hidden);
                    else Check(png.Width == display.Width && png.Height == display.Height && Path.GetDirectoryName(shot.Path) == settings.OutputDirectory,
                        "Configured monitor PNG uses native monitor dimensions and no unrelated application folder: " + hidden);
                    var clip = await recorder.Save();
                    var decoded = await MediaTools.Run("ffmpeg", ["-v", "error", "-i", clip.Path, "-map", "0", "-f", "null", "-"]);
                    Check(recorder.Running && decoded.Code == 0 && recorder.HasReadableBufferData, "Save Rewind still exports a valid playable clip after same-source PNG: " + mode + "/" + hidden);
                    bool failed = false;
                    try { await ScreenshotCapture.Take(request with { Display = display with { Device = "MissingDisplay" } }); } catch (IOException) { failed = true; }
                    Check(failed && recorder.Running && producer.Id == producerId && mute.Muted, "Local source failure neither falls back nor interrupts the active buffer: " + mode + "/" + hidden);
                }
            Check(FfmpegIds().IsSubsetOf(processes), "Completed concurrent tests leave no screenshot/recording child processes");
            // Force timeout/cancellation in the same production process-lifetime mechanism.
            string[] hung = ["-v", "error", "-re", "-f", "lavfi", "-i", "color=s=64x64:r=1", "-an", "-f", "null", "-"];
            bool timedOut = false;
            try { await MediaTools.Run("ffmpeg", hung, 1, bindLifetime: true); } catch (TimeoutException) { timedOut = true; }
            Check(timedOut && FfmpegIds().IsSubsetOf(processes), "Timeout kills/reaps the bound FFmpeg process and closes redirected pipes");
            using (var cancel = new CancellationTokenSource(250))
            {
                bool cancelled = false;
                try { await MediaTools.Run("ffmpeg", hung, 5, cancel.Token, bindLifetime: true); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && FfmpegIds().IsSubsetOf(processes), "Explicit cancellation kills/reaps FFmpeg without orphaning a hung source");
            }
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool cancelled = false;
                try { await ScreenshotCapture.Take(request, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && FfmpegIds().IsSubsetOf(processes), "Pre-cancelled screenshot does not start a capture process");
            }
            var blocked = Path.Combine(directory, "not-a-directory"); File.WriteAllText(blocked, "fixture");
            bool outputFailed = false;
            try { await ScreenshotCapture.Take(request with { OutputDirectory = blocked, GroupByApplication = false }); } catch (IOException) { outputFailed = true; }
            Check(outputFailed && FfmpegIds().IsSubsetOf(processes), "Unavailable output fails before starting capture");
            var missing = game with { Handle = (nint)1, Exe = "MissingScreenshotSource" };
            Check(CaptureSource.ResolveGame(missing, game) == null, "Disappeared explicit application never falls back to last-active or desktop");
            target.Close(); target = null;
            bool disappeared = false;
            try { await ScreenshotCapture.Take(request); } catch (InvalidOperationException) { disappeared = true; }
            Check(disappeared && FfmpegIds().IsSubsetOf(processes), "Disappeared HWND fails locally without starting another source");
            Check(!Directory.GetFiles(directory, "*.partial", SearchOption.AllDirectories).Any() && !Directory.GetFiles(directory, ".publishing-*", SearchOption.AllDirectories).Any(), "Success/error/cancellation paths leave no incomplete image files");
            Check(configBytes == null ? !File.Exists(config) : configBytes.SequenceEqual(File.ReadAllBytes(config)), "Production screenshot commands never alter settings.json");
            captures.Add(new { RealOffPng = first.Path, UngroupedPng = second.Path });
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = true, checks, captures }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { Passed = false, checks, captures, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally { animation.Stop(); target?.Close(); }
    }
}

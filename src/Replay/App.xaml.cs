using System.Diagnostics;
using System.Windows;

namespace Replay;
public partial class App : Application
{
    private Mutex? mutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--microphone-monitor-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await MicrophoneMonitorTest.Run(e.Args.Last())); return;
        }
        if (e.Args.Contains("--recording-edge-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await RecordingTest.RunEdges(e.Args.Last())); return;
        }
        if (e.Args.Contains("--recording-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await RecordingTest.Run(e.Args.Last())); return;
        }
        if (e.Args.Contains("--screenshot-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await ScreenshotTest.Run(e.Args.Last())); return;
        }
        if (e.Args.Contains("--microphone-mute-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await MicrophoneMuteTest.Run(e.Args.Last())); return;
        }
        if (e.Args.Contains("--window-capture-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await WindowCaptureTest.Run(e.Args.Last())); return;
        }
        if (e.Args.Contains("--borderless-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await BorderlessTest.Run(e.Args.Last())); return;
        }
        if (e.Args.Contains("--audio-controls-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await AudioControlsTest.Run(e.Args.Last())); return;
        }
        if (e.Args.Contains("--recovery-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await RecoveryTest.Run(e.Args.Last())); return;
        }
        if (e.Args.Contains("--render-ui"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await SelfTest.RenderUi(e.Args.Last()); Shutdown(); return;
        }
        if (e.Args.Contains("--self-test"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await SelfTest.RunAsync(e.Args));
            return;
        }
        // Share the legacy singleton identity with installed Replay builds.
        mutex = new Mutex(true, "Local\\Replay.Nvidia.Desktop", out var first);
        if (!first) { MessageBox.Show("Rewind уже запущен. Открой значок в трее."); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            Directory.CreateDirectory(Settings.DataDirectory);
            File.AppendAllText(Path.Combine(Settings.DataDirectory, "errors.log"), $"{DateTime.Now:O} {args.Exception}\n");
            MessageBox.Show(args.Exception.Message, "Rewind — ошибка");
            args.Handled = true;
        };
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}

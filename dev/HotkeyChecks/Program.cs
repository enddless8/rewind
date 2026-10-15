using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Replay;

internal static class Program
{
    internal static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static object? Field(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    internal static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    [STAThread]
    private static int Main(string[] args)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "src", "Replay", "Replay.csproj"))) repository = repository.Parent;
        if (repository == null) throw new IOException("Run this harness from the repository.");
        Environment.CurrentDirectory = repository.FullName;
        if (args.Length == 0)
        {
            var modeFile = Path.Combine(AppContext.BaseDirectory, "hotkey-test-mode.txt");
            var mode = File.Exists(modeFile) ? File.ReadAllText(modeFile).Trim() : "live";
            if (mode is not ("live" or "burst" or "checks")) throw new ArgumentException("Unknown test mode.");
            args = [mode, Path.Combine("artifacts", "hotkeys-visible-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))];
        }
        var root = Path.GetFullPath(args[1]);
        if (args[0] is not ("inspect" or "restart" or "checks" or "live" or "burst" or "service-info" or "service-info-restart")) throw new ArgumentException("Unknown test mode.");
        var restart = args[0] is "restart" or "service-info-restart";
        if (!root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a fresh directory inside artifacts.");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("REWIND_HOTKEY_DIAGNOSTICS_FILE", Path.Combine(root, "hotkeys.jsonl"));
        Settings.DataDirectoryForTests = Path.Combine(root, "private");
        ClipFiles.DiagnosticDirectoryForTests = root;
        if (!restart && File.Exists(Path.Combine(Settings.DataDirectory, "settings.json")))
            throw new IOException("Use a fresh output directory; restart is the only mode that reuses settings.");
        if (!restart) new Settings { CaptureMode = "Desktop", AudioMode = "System", MicrophoneId = "off", Discord = false,
            Sounds = false, Height = 720, Fps = 30, BufferSeconds = 15, CompressAfterSave = false,
            OutputDirectory = Path.Combine(root, "output"), BufferDirectory = Path.Combine(root, "buffer") }.Save();
        // Load only the resource dictionary. Instantiating App also queues its real
        // OnStartup and would create an unintended second MainWindow/registrations.
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(repository.FullName, "src", "Replay", "App.xaml"));
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var dictionary = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
                new System.Xml.Linq.XAttribute("xmlns", ns.NamespaceName),
                new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "local", "clr-namespace:Replay;assembly=Rewind"),
                xaml.Root!.Element(ns + "Application.Resources")!.Nodes());
            foreach (var element in dictionary.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:Replay"))
                element.Name = System.Xml.Linq.XName.Get(element.Name.LocalName, "clr-namespace:Replay;assembly=Rewind");
            app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "initialization-error.txt"), ex.ToString()); return 1; }
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var earlyProbes = new List<object>();
                foreach (var key in new[] { 0x79u, 0x77u, 0x78u, 0x70u })
                {
                    var ok = Native.RegisterHotKey(0, 100, 3 | 0x4000, key);
                    earlyProbes.Add(new { key, ok, error = System.Runtime.InteropServices.Marshal.GetLastWin32Error() });
                    if (ok) Native.UnregisterHotKey(0, 100);
                }
                var window = new MainWindow();
                var before = new WindowInteropHelper(window).Handle;
                var lateSourceEvents = 0;
                window.SourceInitialized += (_, _) => lateSourceEvents++;
                window.Show();
                await Task.Delay(300);
                var report = new { BeforeShowHandle = before.ToInt64(), AfterShowHandle = new WindowInteropHelper(window).Handle.ToInt64(),
                    BeforeWindowProbes = earlyProbes, LateSourceEvents = lateSourceEvents, SourceAttached = Field(window, "source") != null,
                    Save = Field(window, "saveRegistration"), Toggle = Field(window, "toggleRegistration"),
                    Recording = Field(window, "recordingRegistration"), Screenshot = Field(window, "screenshotRegistration") };
                File.WriteAllText(Path.Combine(root, "inspection.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                if (args[0].StartsWith("service-info")) await ServiceInfoChecks.Run(window, root, restart);
                else if (args[0] is not ("inspect" or "restart")) await HotkeyChecks.Run(window, root, args[0]);
                await (Task)Call(window, "Exit")!;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(root, "error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            finally { app.Shutdown(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return Environment.ExitCode;
    }
}

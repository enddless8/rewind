using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Replay;

internal static class HotkeyChecks
{
    private static readonly List<object> checks = [];
    private static void Check(bool ok, string name)
    { checks.Add(new { ok, name }); if (!ok) throw new Exception(name); }
    private static Settings Copy(MainWindow w) => Settings.FromJson(JsonSerializer.Serialize(Program.Field(w, "settings")));
    private static bool Commit(MainWindow w, Settings next) => (bool)Program.Call(w, "TryCommitHotkeySettings", next, null)!;
    private static void Render(MainWindow w, string path)
    {
        w.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(w);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    internal static async Task Run(MainWindow w, string root, string mode)
    {
        var events = new List<(string Stage, JsonElement Details)>();
        void Observe(string stage, object? details) => events.Add((stage, JsonSerializer.SerializeToElement(details)));
        HotkeyDiagnostics.Observed += Observe;
        try
        {
            var next = Copy(w);
            next.HotkeyModifiers = next.ToggleHotkeyModifiers = next.RecordingHotkeyModifiers = next.ScreenshotHotkeyModifiers = 6;
            Check(Commit(w, next), "Custom Ctrl+Shift F8/F10/F9/F1 bindings register through the real Windows API");
            Check(new[] { "saveRegistration", "toggleRegistration", "recordingRegistration", "screenshotRegistration" }.All(f => Program.Field(w, f) != null), "All four action registrations active");
            Check(((TextBlock)w.FindName("HotkeyWarningText")).Text == "", "Custom binding status has no false conflicts");

            var duplicate = Copy(w); duplicate.HotkeyKey = duplicate.ToggleHotkeyKey;
            Check(!Commit(w, duplicate), "Duplicate bindings rejected transactionally");
            Check(Copy(w).HotkeyKey == 0x79, "Duplicate attempt preserves existing binding");

            using var owner = new HwndSource(new HwndSourceParameters("Rewind conflict fixture") { WindowStyle = 0 });
            Check(Native.RegisterHotKey(owner.Handle, 100, 7 | 0x4000, 0x86), "Conflict fixture reserves Ctrl+Alt+Shift+F23");
            try
            {
                var unavailable = Copy(w); unavailable.HotkeyModifiers = 7; unavailable.HotkeyKey = 0x86;
                Check(!Commit(w, unavailable), "Windows error 1409 rejects an occupied shortcut");
                Check(((TextBlock)w.FindName("HotkeyEditStatus")).Text.Contains("1409"), "Win32 error visible to user");
                Check(Settings.Load().HotkeyModifiers == 6 && Program.Field(w, "saveRegistration") != null, "Conflict preserves persisted settings and old live registration");
            }
            finally { Native.UnregisterHotKey(owner.Handle, 100); }

            var disabled = Copy(w); disabled.HotkeyEnabled = false;
            Check(Commit(w, disabled) && Program.Field(w, "saveRegistration") == null, "Disabling releases native registration");
            Check(Native.RegisterHotKey(owner.Handle, 100, 6 | 0x4000, 0x79), "Disabled shortcut is available to another HWND");
            Native.UnregisterHotKey(owner.Handle, 100);
            disabled.HotkeyEnabled = true;
            Check(Commit(w, disabled), "Reenable restores native registration");

            var defaults = Copy(w);
            defaults.HotkeyModifiers = defaults.ToggleHotkeyModifiers = defaults.RecordingHotkeyModifiers = defaults.ScreenshotHotkeyModifiers = 3;
            Check(Commit(w, defaults), "All four default Ctrl+Alt shortcuts registered in Windows");

            var legacy = Settings.FromJson("{\"HotkeyModifiers\":3,\"HotkeyKey\":121}");
            var migrated = Copy(w);
            migrated.ToggleHotkeyKey = legacy.ToggleHotkeyKey;
            migrated.RecordingHotkeyKey = legacy.RecordingHotkeyKey;
            migrated.RecordingHotkeyEnabled = legacy.RecordingHotkeyEnabled;
            Check(Commit(w, migrated), "Legacy F9 Toggle / unassigned Recording can transfer a live reservation");
            var oldToggle = Program.Field(w, "toggleRegistration");
            Check(Native.RegisterHotKey(owner.Handle, 102, 3 | 0x4000, 0x77), "Default F8 can be occupied while legacy F9 is active");
            var saved = File.ReadAllText(Path.Combine(Settings.DataDirectory, "settings.json"));
            Program.Call(w, "RestoreHotkeyDefaults_Click", null, new RoutedEventArgs());
            Check(Copy(w).ToggleHotkeyKey == 0x78 && ReferenceEquals(oldToggle, Program.Field(w, "toggleRegistration")) &&
                File.ReadAllText(Path.Combine(Settings.DataDirectory, "settings.json")) == saved, "Failed restore preserves legacy settings and registration");
            Native.UnregisterHotKey(owner.Handle, 102);
            Program.Call(w, "RestoreHotkeyDefaults_Click", null, new RoutedEventArgs());
            Check(Copy(w).ToggleHotkeyKey == 0x77 && Copy(w).RecordingHotkeyEnabled &&
                ReferenceEquals(oldToggle, Program.Field(w, "recordingRegistration")), "Restore defaults moves legacy F9 to Recording without releasing its reservation");

            var noToggle = Copy(w); noToggle.ToggleHotkeyEnabled = false;
            Check(Commit(w, noToggle), "Release Toggle for startup-conflict fixture");
            Check(Native.RegisterHotKey(owner.Handle, 104, 3 | 0x4000, 0x77), "External fixture occupies default Toggle");
            var wantedToggle = Copy(w); wantedToggle.ToggleHotkeyEnabled = true;
            typeof(MainWindow).GetField("settings", Program.Private)!.SetValue(w, wantedToggle);
            Program.Call(w, "RegisterKeys");
            Check(Program.Field(w, "toggleRegistration") == null &&
                ((TextBlock)w.FindName("HotkeyWarningText")).Text.Contains("F8") &&
                ((System.Windows.Controls.CheckBox)w.FindName("ToggleHotkeyEnabledCheck")).IsChecked == true,
                "Startup conflict remains visible without falsifying enabled preference");
            Render(w, Path.Combine(root, "conflict.png"));
            Native.UnregisterHotKey(owner.Handle, 104);
            Program.Call(w, "RetryHotkeys_Click", null, new RoutedEventArgs());
            Check(Program.Field(w, "toggleRegistration") != null && ((TextBlock)w.FindName("HotkeyWarningText")).Text == "",
                "Retry recovers rejected registration after external owner releases it");
            wantedToggle.Save();
            Program.Call(w, "Settings_Click", null, new RoutedEventArgs());
            ((System.Windows.Controls.RadioButton)w.FindName("HotkeysNav")).IsChecked = true;
            Render(w, Path.Combine(root, "settings.png"));
            Program.Call(w, "Back_Click", null, new RoutedEventArgs());

            using (var locked = new FileStream(Path.Combine(Settings.DataDirectory, "settings.json.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var failSave = Copy(w); failSave.HotkeyModifiers = 6; failSave.HotkeyKey = 0x7A;
                Check(!Commit(w, failSave), "Settings write failure rolls back reserved replacement");
                Check(Copy(w).HotkeyModifiers == 3 && Native.RegisterHotKey(owner.Handle, 103, 6 | 0x4000, 0x7A), "Old shortcut retained and failed replacement released");
                Native.UnregisterHotKey(owner.Handle, 103);
            }

            foreach (var phase in mode == "live" ? new[] { "focused", "desktop", "minimized", "tray" } : Array.Empty<string>())
            {
                if (phase == "focused") { w.Show(); w.Activate(); }
                if (phase == "minimized") w.WindowState = WindowState.Minimized;
                if (phase == "tray") w.Close(); // Exercise the real close-to-tray handler.
                var index = events.Count;
                File.WriteAllText(Path.Combine(root, "phase.txt"), phase + ": press Ctrl+Alt+F8, F9, F1, F10 in that order; focus Explorer for desktop/minimized/tray");
                var until = Stopwatch.StartNew();
                while (events.Skip(index).Where(e => e.Stage == "command-start").Select(e => e.Details.GetProperty("Action").GetString()).Distinct().Count() < 4)
                {
                    if (until.Elapsed > TimeSpan.FromMinutes(5)) throw new TimeoutException("Input missing: " + phase);
                    await Task.Delay(100);
                }
                while ((bool)Program.Field(w, "busy")! || (bool)Program.Field(w, "screenshotCapturing")! ||
                    (int)Program.Field(w, "hotkeyCommands")!.GetType().GetProperty("Count")!.GetValue(Program.Field(w, "hotkeyCommands"))! > 0)
                {
                    if (until.Elapsed > TimeSpan.FromMinutes(5)) throw new TimeoutException("Command did not finish: " + phase);
                    await Task.Delay(100);
                }
                Check(events.Skip(index).Count(e => e.Stage == "message") >= 4, phase + ": real WM_HOTKEY dispatch for all four actions");
                Check(!events.Skip(index).Any(e => e.Stage == "command-failed"), phase + ": shared commands return without unobserved exceptions");
                if (phase == "minimized") Check(w.WindowState == WindowState.Minimized, "Hotkeys keep minimized window minimized");
                if (phase == "tray") Check(!w.IsVisible, "Hotkeys keep tray window hidden");
                File.WriteAllText(Path.Combine(root, phase + "-events.json"), JsonSerializer.Serialize(events.Skip(index), new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            }
            if (mode == "burst")
            {
                await (Task)Program.Call(w, "Toggle")!;
                await (Task)Program.Call(w, "StartRecording")!;
                await Task.Delay(6000);
                var index = events.Count;
                File.WriteAllText(Path.Combine(root, "phase.txt"), "burst: press Ctrl+Alt+F10 six times rapidly while Explorer has focus");
                var deadline = Stopwatch.StartNew();
                while (events.Skip(index).Count(e => e.Stage == "message") < 6)
                { if (deadline.Elapsed.TotalMinutes > 5) throw new TimeoutException("Burst input missing"); await Task.Delay(50); }
                while (w.ConventionalRecording.State != RecordingState.Recording ||
                    (int)Program.Field(w, "saveCommands")!.GetType().GetProperty("Count")!.GetValue(Program.Field(w, "saveCommands"))! != 0)
                { if (deadline.Elapsed.TotalMinutes > 5) throw new TimeoutException("Burst commands not drained"); await Task.Delay(50); }
                Check(events.Skip(index).Any(e => e.Stage == "save-accepted") && events.Skip(index).Any(e => e.Stage == "save-published"), "Rapid native SaveClip requests publish validated media");
                Check(w.ConventionalRecording.State == RecordingState.Recording, "Rapid saves leave conventional recording active");
                Check(events.Skip(index).Any(e => e.Stage == "overlay-shown" && e.Details.GetProperty("Kind").GetString() == "RewindSaved"), "Save success notification follows media publication");
                await (Task)Program.Call(w, "StopRecording")!;
                await (Task)Program.Call(w, "Toggle")!;
                var custom = Copy(w); custom.ScreenshotHotkeyModifiers = 6; custom.ScreenshotHotkeyKey = 0x71;
                Check(Commit(w, custom), "Changed screenshot binding registered");
                index = events.Count;
                File.WriteAllText(Path.Combine(root, "phase.txt"), "custom: press Ctrl+Shift+F2 with Explorer focused");
                deadline.Restart();
                while (!events.Skip(index).Any(e => e.Stage == "screenshot-published"))
                { if (deadline.Elapsed.TotalMinutes > 5) throw new TimeoutException("Custom input missing"); await Task.Delay(50); }
                Check(events.Skip(index).Any(e => e.Stage == "message"), "Custom shortcut dispatches through real WM_HOTKEY and publishes screenshot");
            }
            Program.Call(w, "ShowWindow");
            Check(w.IsVisible && w.WindowState == WindowState.Normal, "Tray restore preserves registrations");
            var active = Copy(w);
            Program.Call(w, "DetachHotkeys");
            Check(Native.RegisterHotKey(owner.Handle, 101, 3 | 0x4000, 0x79), "Shutdown cleanup releases shortcut to another window");
            Native.UnregisterHotKey(owner.Handle, 101);
            // The normal restart path reads Settings.Load, not the test object.
            Check(Settings.Load().HotkeyModifiers == active.HotkeyModifiers && Settings.Load().HotkeyKey == active.HotkeyKey, "Custom bindings persist for restart");
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(root, "phase.txt"), "complete");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString(), checks }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally { HotkeyDiagnostics.Observed -= Observe; }
    }
}

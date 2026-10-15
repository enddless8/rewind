using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Replay;

public partial class MainWindow
{
    private enum HotkeyAction { Save, Toggle, Recording, Screenshot, Microphone }
    private sealed record HotkeyRegistration(int Id, uint Modifiers, uint Key);
    private HotkeyRegistration? saveRegistration, toggleRegistration, recordingRegistration, screenshotRegistration, microphoneRegistration;
    private HotkeyAction? editingHotkey;
    private readonly Dictionary<HotkeyAction, string> hotkeyErrors = new();
    private readonly HashSet<HotkeyAction> hotkeyCommands = new();
    private readonly Dictionary<HotkeyAction, long> lastHotkeyCommands = new();
    private readonly HashSet<int> nativeHotkeyIds = new();
    private static string ActionText(HotkeyAction action) => action switch
    {
        HotkeyAction.Save => "Сохранить Rewind", HotkeyAction.Toggle => "Включить Rewind",
        HotkeyAction.Recording => "Запись", HotkeyAction.Screenshot => "Скриншот", _ => "Микрофон"
    };

    private void UpdateHotkeyWarnings()
    {
        hotkeyWarning = hotkeyErrors.Count == 0 ? null : string.Join("\n", hotkeyErrors.Select(e => $"{ActionText(e.Key)}: {e.Value}"));
        HotkeyWarningText.Text = hotkeyWarning == null ? "" : "Горячие клавиши недоступны. Настройки → Горячие клавиши.\n" + hotkeyWarning;
        HotkeyEditStatus.Text = hotkeyWarning ?? "";
    }

    private void RetryHotkeys_Click(object sender, RoutedEventArgs e) => RegisterKeys();

    private void RestoreHotkeyDefaults_Click(object sender, RoutedEventArgs e)
    {
        CancelHotkeyEdit();
        var next = CopyHotkeySettings();
        var defaults = new Settings();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var value = HotkeyValue(defaults, action);
            SetHotkeyValue(next, action, value.Enabled, value.Modifiers, value.Key);
        }
        TryCommitHotkeySettings(next, null);
    }

    private async Task ExecuteHotkey(HotkeyAction action, long received)
    {
        Dispatcher.VerifyAccess();
        var name = action.ToString();
        // Save has its own admission guard and bounded queue; do not serialize exports.
        var exclusive = action != HotkeyAction.Save;
        var toggle = action is HotkeyAction.Toggle or HotkeyAction.Recording or HotkeyAction.Microphone;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (exiting || editingHotkey != null || Registration(action) == null ||
            exclusive && hotkeyCommands.Contains(action) ||
            toggle && lastHotkeyCommands.TryGetValue(action, out var last) && System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalMilliseconds < 350)
        { HotkeyDiagnostics.Mark("command-ignored", new { Action = name, Reason = "exiting/editing/unregistered/in-flight/debounce" }); return; }
        if (exclusive) hotkeyCommands.Add(action);
        lastHotkeyCommands[action] = now;
        HotkeyDiagnostics.Mark("command-start", new { Action = name, Received = received });
        try
        {
            switch (action)
            {
                case HotkeyAction.Save: await SaveClip(received, "hotkey"); break;
                case HotkeyAction.Toggle: if (!recovering) await Toggle(); break;
                case HotkeyAction.Recording: await ToggleRecording(); break;
                case HotkeyAction.Screenshot: await TakeScreenshot(); break;
                case HotkeyAction.Microphone: ToggleMicrophoneMuted(); break;
            }
            // Shared commands report their own success/errors. Returning alone is not success.
            HotkeyDiagnostics.Mark("command-returned", new { Action = name, recordingWanted, busy, recovering,
                Recording = ConventionalRecording.State.ToString(), RecordingError = ConventionalRecording.Error, rewindCommandError, LastFeedback = LastClipText.Text });
        }
        catch (Exception ex)
        {
            ClipFiles.Log("hotkey-command", ex, name);
            HotkeyDiagnostics.Mark("command-failed", new { Action = name, Error = ex.ToString() });
            Notify("Ошибка горячей клавиши", ActionText(action) + ": " + ex.Message, true);
        }
        finally { if (exclusive) hotkeyCommands.Remove(action); }
    }

    private static (bool Enabled, uint Modifiers, uint Key) HotkeyValue(Settings value, HotkeyAction action) => action switch
    {
        HotkeyAction.Save => (value.HotkeyEnabled, value.HotkeyModifiers, value.HotkeyKey),
        HotkeyAction.Toggle => (value.ToggleHotkeyEnabled, value.ToggleHotkeyModifiers, value.ToggleHotkeyKey),
        HotkeyAction.Recording => (value.RecordingHotkeyEnabled, value.RecordingHotkeyModifiers, value.RecordingHotkeyKey),
        HotkeyAction.Screenshot => (value.ScreenshotHotkeyEnabled, value.ScreenshotHotkeyModifiers, value.ScreenshotHotkeyKey),
        HotkeyAction.Microphone => (value.MicrophoneHotkeyEnabled, value.MicrophoneHotkeyModifiers, value.MicrophoneHotkeyKey),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    private static void SetHotkeyValue(Settings value, HotkeyAction action, bool enabled, uint modifiers, uint key)
    {
        switch (action)
        {
            case HotkeyAction.Save: value.HotkeyEnabled = enabled; value.HotkeyModifiers = modifiers; value.HotkeyKey = key; break;
            case HotkeyAction.Toggle: value.ToggleHotkeyEnabled = enabled; value.ToggleHotkeyModifiers = modifiers; value.ToggleHotkeyKey = key; break;
            case HotkeyAction.Recording: value.RecordingHotkeyEnabled = enabled; value.RecordingHotkeyModifiers = modifiers; value.RecordingHotkeyKey = key; break;
            case HotkeyAction.Screenshot: value.ScreenshotHotkeyEnabled = enabled; value.ScreenshotHotkeyModifiers = modifiers; value.ScreenshotHotkeyKey = key; break;
            case HotkeyAction.Microphone: value.MicrophoneHotkeyEnabled = enabled; value.MicrophoneHotkeyModifiers = modifiers; value.MicrophoneHotkeyKey = key; break;
        }
    }
    private HotkeyRegistration? Registration(HotkeyAction action) => action switch
    {
        HotkeyAction.Save => saveRegistration, HotkeyAction.Toggle => toggleRegistration,
        HotkeyAction.Recording => recordingRegistration, HotkeyAction.Screenshot => screenshotRegistration,
        HotkeyAction.Microphone => microphoneRegistration, _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    private void SetRegistration(HotkeyAction action, HotkeyRegistration? registration)
    {
        switch (action)
        {
            case HotkeyAction.Save: saveRegistration = registration; break;
            case HotkeyAction.Toggle: toggleRegistration = registration; break;
            case HotkeyAction.Recording: recordingRegistration = registration; break;
            case HotkeyAction.Screenshot: screenshotRegistration = registration; break;
            case HotkeyAction.Microphone: microphoneRegistration = registration; break;
        }
    }
    private static void ValidateHotkey(uint modifiers, uint key)
    {
        if (modifiers == 0 || (modifiers & ~7u) != 0 || key < 0x20 || key > 0xFE ||
            key is 0x5B or 0x5C or >= 0xA0 and <= 0xA5 || !Enum.IsDefined((System.Windows.Forms.Keys)key))
            throw new ArgumentException("Хоткею нужна обычная клавиша и Ctrl, Alt или Shift.");
    }

    private void ReleaseHotkey(HotkeyRegistration? registration)
    {
        if (source != null && registration != null)
        {
            Dispatcher.VerifyAccess();
            var ok = Native.UnregisterHotKey(source.Handle, registration.Id);
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            HotkeyDiagnostics.Mark("unregister", new { registration.Id, Success = ok, Error = ok ? 0 : error });
            if (ok) nativeHotkeyIds.Remove(registration.Id);
            if (!ok) ClipFiles.Log("hotkey-unregister", new System.ComponentModel.Win32Exception(error), $"Id={registration.Id}; HWND={source.Handle}");
        }
    }

    private void DetachHotkeys()
    {
        Dispatcher.VerifyAccess();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        { ReleaseHotkey(Registration(action)); SetRegistration(action, null); }
        source?.RemoveHook(WindowProc);
        source = null;
    }

    // Keep the old registration until its replacement and config save have both succeeded.
    // IDs belong to native reservations, so a binding can transfer between actions
    // (including legacy F9 Toggle -> Recording) without temporarily releasing it.
    private HotkeyRegistration? ReserveHotkey(HotkeyRegistration? current, bool enabled, uint modifiers, uint key, HotkeyAction action)
    {
        Dispatcher.VerifyAccess();
        if (!enabled) return null;
        ValidateHotkey(modifiers, key);
        if (current?.Modifiers == modifiers && current.Key == key) return current;
        if (source == null) throw new InvalidOperationException("Окно ещё не готово. Попробуй снова.");
        var id = Enumerable.Range(1, 0xBFFF).First(candidate => !nativeHotkeyIds.Contains(candidate));
        var registered = Native.RegisterHotKey(source.Handle, id, modifiers | 0x4000, key);
        var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        HotkeyDiagnostics.Mark("register", new { Id = id, Modifiers = modifiers, Key = key, Hwnd = source.Handle.ToInt64(), Success = registered, Error = registered ? 0 : error });
        if (!registered)
        {
            ClipFiles.Log("hotkey-register", new System.ComponentModel.Win32Exception(error),
                $"Action={action}; Shortcut={HotkeyText(modifiers, key)}; Enabled={enabled}; Id={id}; HWND={source.Handle}");
            throw new InvalidOperationException($"{HotkeyText(modifiers, key)}: {new System.ComponentModel.Win32Exception(error).Message} (код {error}). Выбери свободное сочетание или освободи его в другом приложении и нажми «Повторить регистрацию». Сохранённое назначение не изменено.");
        }
        nativeHotkeyIds.Add(id);
        return new(id, modifiers, key);
    }

    private void RegisterConfiguredHotkey(HotkeyAction action)
    {
        var current = Registration(action);
        var value = HotkeyValue(settings, action);
        try
        {
            if (value.Enabled && Enum.GetValues<HotkeyAction>().Any(other => other != action &&
                Registration(other) is { } active && active.Modifiers == value.Modifiers && active.Key == value.Key))
                throw new ArgumentException("Это сочетание уже назначено другому действию Rewind.");
            var next = ReserveHotkey(current, value.Enabled, value.Modifiers, value.Key, action);
            if (!ReferenceEquals(current, next)) ReleaseHotkey(current);
            SetRegistration(action, next);
            hotkeyErrors.Remove(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Do not rewrite the user's file merely because Windows rejected a key at startup.
            // Registration availability is not the user's enabled preference. Keep
            // that preference so editing a rejected startup shortcut can recover it.
            if (current != null) SetHotkeyValue(settings, action, true, current.Modifiers, current.Key);
            hotkeyErrors[action] = ex.Message;
            ClipFiles.Log("hotkey-config", ex, ActionText(action));
        }
    }

    private bool CommitHotkeySettings(Settings next) => TryCommitHotkeySettings(next, editingHotkey);

    private bool TryCommitHotkeySettings(Settings next, HotkeyAction? requestedAction)
    {
        var replacements = new Dictionary<HotkeyAction, HotkeyRegistration?>();
        var previous = Enum.GetValues<HotkeyAction>().Select(Registration).OfType<HotkeyRegistration>().ToHashSet();
        try
        {
            var actions = Enum.GetValues<HotkeyAction>();
            // Preserve Phase 5 semantics: assigned combinations remain reserved in the
            // configuration even when OFF; an unassigned migrated row has Key == 0.
            foreach (var action in actions)
            {
                var value = HotkeyValue(next, action);
                if (value.Key != 0) ValidateHotkey(value.Modifiers, value.Key);
                else if (value.Enabled) throw new ArgumentException("Сначала назначь сочетание через «Изменить».");
                if (actions.Any(other => other != action && value.Key != 0 &&
                    HotkeyValue(next, other).Modifiers == value.Modifiers && HotkeyValue(next, other).Key == value.Key))
                    throw new ArgumentException("Это сочетание уже назначено другому действию Rewind.");
            }
            next.Validate();
            foreach (var action in actions)
            {
                var value = HotkeyValue(next, action);
                if (requestedAction == action || value != HotkeyValue(settings, action))
                    replacements[action] = value.Enabled ? previous.FirstOrDefault(r => r.Modifiers == value.Modifiers && r.Key == value.Key)
                        ?? ReserveHotkey(Registration(action), true, value.Modifiers, value.Key, action) : null;
            }
            next.Save();
            settings = next;
            foreach (var (action, replacement) in replacements)
                SetRegistration(action, replacement);
            var retained = Enum.GetValues<HotkeyAction>().Select(Registration).OfType<HotkeyRegistration>().ToHashSet();
            foreach (var old in previous.Except(retained)) ReleaseHotkey(old);
            foreach (var action in replacements.Keys) hotkeyErrors.Remove(action);
            UpdateHotkeyWarnings();
            return true;
        }
        catch (Exception ex)
        {
            ClipFiles.Log("hotkey-settings", ex);
            foreach (var (action, replacement) in replacements)
                if (replacement != null && !previous.Contains(replacement)) ReleaseHotkey(replacement);
            HotkeyEditStatus.Text = ex is ArgumentException or InvalidOperationException ? ex.Message
                : "Не удалось сохранить настройки. Предыдущее сочетание осталось активным.";
            return false;
        }
        finally { RefreshHotkeyControls(); }
    }

    private Settings CopyHotkeySettings() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;

    private void RefreshHotkeyControls()
    {
        HotkeyBox.Text = HotkeyText(settings.HotkeyModifiers, settings.HotkeyKey);
        ToggleHotkeyBox.Text = HotkeyText(settings.ToggleHotkeyModifiers, settings.ToggleHotkeyKey);
        RecordingHotkeyBox.Text = HotkeyText(settings.RecordingHotkeyModifiers, settings.RecordingHotkeyKey);
        ScreenshotHotkeyBox.Text = HotkeyText(settings.ScreenshotHotkeyModifiers, settings.ScreenshotHotkeyKey);
        MicrophoneHotkeyBox.Text = HotkeyText(settings.MicrophoneHotkeyModifiers, settings.MicrophoneHotkeyKey);
        SaveHotkeyEnabledCheck.IsChecked = settings.HotkeyEnabled;
        ToggleHotkeyEnabledCheck.IsChecked = settings.ToggleHotkeyEnabled;
        RecordingHotkeyEnabledCheck.IsChecked = settings.RecordingHotkeyEnabled;
        ScreenshotHotkeyEnabledCheck.IsChecked = settings.ScreenshotHotkeyEnabled;
        MicrophoneHotkeyEnabledCheck.IsChecked = settings.MicrophoneHotkeyEnabled;
        foreach (var (control, action) in new[] { (SaveHotkeyEnabledCheck, HotkeyAction.Save), (ToggleHotkeyEnabledCheck, HotkeyAction.Toggle),
            (RecordingHotkeyEnabledCheck, HotkeyAction.Recording), (ScreenshotHotkeyEnabledCheck, HotkeyAction.Screenshot), (MicrophoneHotkeyEnabledCheck, HotkeyAction.Microphone) })
            control.ToolTip = hotkeyErrors.GetValueOrDefault(action) ?? (Registration(action) != null ? "Зарегистрировано в Windows" : "Выключено");
        HotkeyHint.Text = "Работают и при свёрнутом окне. Изменить — нажми сочетание; Esc — отмена. «—» — не назначено; выбери свободное сочетание и включи переключатель.";
    }

    private TextBox EditingHotkeyBox => editingHotkey switch
    { HotkeyAction.Toggle => ToggleHotkeyBox, HotkeyAction.Recording => RecordingHotkeyBox, HotkeyAction.Screenshot => ScreenshotHotkeyBox, HotkeyAction.Microphone => MicrophoneHotkeyBox, _ => HotkeyBox };

    private void HotkeyEdit_Click(object sender, RoutedEventArgs e)
    {
        RefreshHotkeyControls();
        editingHotkey = Enum.Parse<HotkeyAction>((string)((Button)sender).Tag);
        HotkeyEditStatus.Text = "Нажми клавишу вместе с Ctrl, Alt или Shift. Esc — отмена.";
        EditingHotkeyBox.Text = "Нажмите сочетание…";
        EditingHotkeyBox.Focus();
    }

    private void CancelHotkeyEdit()
    {
        if (editingHotkey == null) return;
        editingHotkey = null;
        HotkeyEditStatus.Text = hotkeyWarning ?? "";
        RefreshHotkeyControls();
    }

    private void HotkeyFields_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (HotkeyBox != null && !HotkeyFields.IsVisible) CancelHotkeyEdit();
    }

    private void HotkeyFields_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => { if (!HotkeyFields.IsKeyboardFocusWithin) CancelHotkeyEdit(); });
    }

    private void HotkeyFields_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (editingHotkey == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { CancelHotkeyEdit(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            // WM_HOTKEY can arrive before queued modifier KeyDown events. Do not
            // overwrite a conflict reported for that same chord with a hint.
            if (EditingHotkeyBox.Text == "Нажмите сочетание…")
                HotkeyEditStatus.Text = "Нужна обычная клавиша вместе с Ctrl, Alt или Shift.";
            return;
        }
        if (e.IsRepeat) return;
        var modifiers = Keyboard.Modifiers;
        uint nativeModifiers = (modifiers.HasFlag(ModifierKeys.Control) ? 2u : 0u) |
                               (modifiers.HasFlag(ModifierKeys.Alt) ? 1u : 0u) |
                               (modifiers.HasFlag(ModifierKeys.Shift) ? 4u : 0u);
        if (modifiers.HasFlag(ModifierKeys.Windows)) { HotkeyEditStatus.Text = "Используй Ctrl, Alt или Shift."; return; }
        CaptureHotkey(HotkeyText(nativeModifiers, (uint)KeyInterop.VirtualKeyFromKey(key)));
    }

    private void CaptureHotkey(string text)
    {
        if (editingHotkey == null) return;
        EditingHotkeyBox.Text = text;
        try
        {
            var hotkey = ParseHotkey(text);
            var next = CopyHotkeySettings();
            SetHotkeyValue(next, editingHotkey.Value, HotkeyValue(next, editingHotkey.Value).Enabled, hotkey.Modifiers, hotkey.Key);
            if (CommitHotkeySettings(next)) editingHotkey = null;
            else EditingHotkeyBox.Text = text;
        }
        catch (ArgumentException ex) { HotkeyEditStatus.Text = ex.Message; }
    }

    private void HotkeyEnabled_Click(object sender, RoutedEventArgs e)
    {
        CancelHotkeyEdit();
        var control = (CheckBox)sender;
        var next = CopyHotkeySettings();
        var action = Enum.Parse<HotkeyAction>((string)control.Tag);
        var value = HotkeyValue(next, action);
        SetHotkeyValue(next, action, control.IsChecked == true, value.Modifiers, value.Key);
        TryCommitHotkeySettings(next, action);
    }
}

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
    private static int PrimaryHotkeyId(HotkeyAction action) => action switch
    { HotkeyAction.Save => 1, HotkeyAction.Toggle => 2, HotkeyAction.Recording => 5, HotkeyAction.Screenshot => 6, HotkeyAction.Microphone => 9, _ => throw new ArgumentOutOfRangeException(nameof(action)) };

    private static void ValidateHotkey(uint modifiers, uint key)
    {
        if (modifiers == 0 || (modifiers & ~7u) != 0 || key < 0x20 || key > 0xFE ||
            key is 0x5B or 0x5C or >= 0xA0 and <= 0xA5 || !Enum.IsDefined((System.Windows.Forms.Keys)key))
            throw new ArgumentException("Хоткею нужна обычная клавиша и Ctrl, Alt или Shift.");
    }

    private void ReleaseHotkey(HotkeyRegistration? registration)
    {
        if (source != null && registration != null)
            Native.UnregisterHotKey(source.Handle, registration.Id);
    }

    // Keep the old registration until its replacement and config save have both succeeded.
    // Two IDs per action let RegisterHotKey reserve the new combination first.
    private HotkeyRegistration? ReserveHotkey(HotkeyRegistration? current, bool enabled, uint modifiers, uint key, int primaryId)
    {
        if (!enabled) return null;
        ValidateHotkey(modifiers, key);
        if (current?.Modifiers == modifiers && current.Key == key) return current;
        if (source == null) throw new InvalidOperationException("Окно ещё не готово. Попробуй снова.");
        var id = current?.Id == primaryId ? primaryId + 2 : primaryId;
        var registered = Native.RegisterHotKey(source.Handle, id, modifiers | 0x4000, key);
        var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (!registered)
        {
            var action = Enum.GetValues<HotkeyAction>().Single(a => PrimaryHotkeyId(a) == primaryId);
            ClipFiles.Log("hotkey-register", new System.ComponentModel.Win32Exception(error),
                $"Action={action}; Shortcut={HotkeyText(modifiers, key)}; Enabled={enabled}; Id={id}; HWND={source.Handle}");
            throw new InvalidOperationException($"{HotkeyText(modifiers, key)} недоступно: сочетание занято или запрещено Windows (код {error}). Предыдущее сохранено.");
        }
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
            var next = ReserveHotkey(current, value.Enabled, value.Modifiers, value.Key, PrimaryHotkeyId(action));
            if (!ReferenceEquals(current, next)) ReleaseHotkey(current);
            SetRegistration(action, next);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Do not rewrite the user's file merely because Windows rejected a key at startup.
            // Registration availability is not the user's enabled preference. Keep
            // that preference so editing a rejected startup shortcut can recover it.
            if (current != null) SetHotkeyValue(settings, action, true, current.Modifiers, current.Key);
            hotkeyWarning = ex.Message;
        }
    }

    private bool CommitHotkeySettings(Settings next) => TryCommitHotkeySettings(next, editingHotkey);

    private bool TryCommitHotkeySettings(Settings next, HotkeyAction? requestedAction)
    {
        var replacements = new Dictionary<HotkeyAction, HotkeyRegistration?>();
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
                    replacements[action] = ReserveHotkey(Registration(action), value.Enabled, value.Modifiers, value.Key, PrimaryHotkeyId(action));
            }
            next.Save();
            settings = next;
            foreach (var (action, replacement) in replacements)
            {
                if (!ReferenceEquals(replacement, Registration(action))) ReleaseHotkey(Registration(action));
                SetRegistration(action, replacement);
            }
            hotkeyWarning = null;
            HotkeyEditStatus.Text = hotkeyWarning ?? "";
            return true;
        }
        catch (Exception ex)
        {
            foreach (var (action, replacement) in replacements)
                if (!ReferenceEquals(replacement, Registration(action))) ReleaseHotkey(replacement);
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
        SaveHotkeyEnabledCheck.IsChecked = settings.HotkeyEnabled && (source == null || saveRegistration != null);
        ToggleHotkeyEnabledCheck.IsChecked = settings.ToggleHotkeyEnabled && (source == null || toggleRegistration != null);
        RecordingHotkeyEnabledCheck.IsChecked = settings.RecordingHotkeyEnabled && (source == null || recordingRegistration != null);
        ScreenshotHotkeyEnabledCheck.IsChecked = settings.ScreenshotHotkeyEnabled && (source == null || screenshotRegistration != null);
        MicrophoneHotkeyEnabledCheck.IsChecked = settings.MicrophoneHotkeyEnabled && (source == null || microphoneRegistration != null);
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

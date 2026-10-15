using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Forms = System.Windows.Forms;

namespace Replay;
public record MicrophoneChoice(string Id, string Name)
{
    public override string ToString() => Name;
}
public partial class MainWindow : Window
{
    private Settings settings = Settings.Load();
    private Recorder? recorder;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly System.Drawing.Icon titleBarIcon;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private HwndSource? source;
    private bool exiting, busy, saving, ticking;
    private readonly HashSet<Task> saveCommands = new();
    private Guid? latestSaveOperation;
    private GameWindow? foregroundGame;
    private string? lastWarning;
    private string? hotkeyWarning;
    private DateTime lastNotice;
    private readonly CompressionQueue compression;
    private readonly CaptureRecovery recovery = new();
    private bool recordingWanted;
    private bool recovering, saveAfterRecovery;
    private double healthySince;
    private RewindState? rewindTransition;
    private string? rewindCommandError;
    private RewindPresentation rewindPresentation;
    private readonly MicrophoneMuteControl microphoneMute = new();
    private MicrophoneRuntimeState microphoneRuntimeState;
    public MainWindow()
    {
        InitializeComponent();
        overlays = new OverlayNotificationService(Dispatcher, OverlayTarget);
        compression = new CompressionQueue();
        compression.Started += job => Dispatcher.BeginInvoke(() => CompressionOverlayStarted(job));
        compression.Finished += job => Dispatcher.BeginInvoke(() =>
        {
            CompressionOverlayFinished(job);
            if (job.State == "Failed") { Notify("Сжатие не удалось — оригинал сохранён", job.Error ?? "Смотри журнал Rewind", true); return; }
            var percent = job.OriginalBytes > 0 ? 100.0 * (job.OriginalBytes - job.CompressedBytes) / job.OriginalBytes : 0;
            Notify("MP4 готов", percent > 0 ? $"На {percent:F0}% меньше · {job.Output}" : $"Размер не уменьшился; оригинал сохранён. {job.Output}", false);
        });
        using (var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Rewind;component/assets/tray/RewindTray.ico"))!.Stream)
        using (var loadedIcon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize))
            trayIcon = (System.Drawing.Icon)loadedIcon.Clone();
        titleBarIcon = (System.Drawing.Icon)trayIcon.Clone();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Rewind — выключен", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть Rewind", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Включить / выключить буфер", null, async (_, _) => await Toggle());
        menu.Items.Add("Сохранить клип", null, async (_, _) => await SaveClip());
        menu.Items.Add("Папка клипов", null, (_, _) => OpenFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, async (_, _) => await Exit());
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        RefreshSources(); LoadSettings();
        IncompleteRecordings = Recorder.FindIncompleteRecordings(settings.BufferDirectory);
        if (IncompleteRecordings.Count > 0)
            Notify("Найдена незавершённая запись", $"Найдено сессий: {IncompleteRecordings.Count}. Временные данные оставлены в папке буфера для восстановления.", true);
        InitializeAudioControls();
        SourceInitialized += (_, _) => { source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source.AddHook(WindowProc); RegisterKeys(); };
        SourceInitialized += (_, _) => { ApplyDarkWindowChrome(); ApplyTitleBarIcon(); };
        Loaded += (_, _) => ApplyTitleBarIcon();
        Closed += (_, _) => { DetachHotkeys(); titleBarIcon.Dispose(); };
        Closing += OnClosing;
        Deactivated += (_, _) => { CancelHotkeyEdit(); CloseRewindSelector(); };
        timer.Tick += async (_, _) => await Tick(); timer.Start();
        GamePicker.SelectionChanged += (_, _) => { if (GamePicker.SelectedItem is GameWindow g) GameNameBox.Text = settings.GameNames.GetValueOrDefault(g.Exe, ""); };
        UpdateMainPresentation();
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendIconMessage(nint hwnd, uint message, nint kind, nint icon);
    private void ApplyTitleBarIcon()
    {
        // Override only the native caption's small icon. WPF still owns its
        // original Window.Icon and ICON_BIG; never destroy its returned handle.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) _ = SendIconMessage(hwnd, 0x0080 /* WM_SETICON */, 0 /* ICON_SMALL */, titleBarIcon.Handle);
    }
    private void ApplyDarkWindowChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // DWM colors the native frame; Windows still owns dragging, resizing and caption buttons.
        var dark = 1;
        var background = 0x1E1E1E;
        var foreground = 0xFFFFFF;
        _ = DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref background, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref foreground, sizeof(int));
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        CloseRewindSelector();
        CaptureNav.IsChecked = true;
        SettingsScroll.ScrollToTop();
        MainView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
    }
    private void DurationSlider_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!int.TryParse(DurationBox.Text, out var seconds)) return;
        var next = e.Key switch
        {
            System.Windows.Input.Key.Left or System.Windows.Input.Key.Down => seconds - 1,
            System.Windows.Input.Key.Right or System.Windows.Input.Key.Up => seconds + 1,
            System.Windows.Input.Key.PageDown => seconds - 10,
            System.Windows.Input.Key.PageUp => seconds + 10,
            System.Windows.Input.Key.Home => 15,
            System.Windows.Input.Key.End => 600,
            _ => seconds
        };
        if (next == seconds && e.Key is not (System.Windows.Input.Key.Home or System.Windows.Input.Key.End)) return;
        DurationBox.Text = Math.Clamp(next, 15, 600).ToString();
        e.Handled = true;
    }
    private void DurationLandmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: { } tag } && int.TryParse(tag.ToString(), out var seconds))
        {
            DurationBox.Text = seconds.ToString();
            DurationSlider.Focus();
        }
    }
    private void SettingsNavigation_Checked(object sender, RoutedEventArgs e) => SettingsScroll?.ScrollToTop();
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
        UpdateMainPresentation();
        SettingsGear.Focus();
    }
    // One shared presentation for the selector, save button, tray and existing hotkeys.
    private void UpdateMainPresentation()
    {
        var running = recordingWanted && recorder?.Running == true;
        rewindPresentation = RewindPresentation.FromCapture(recordingWanted, running, rewindTransition,
            recovering, recorder?.RecoverableCaptureLoss == true, recovery.Exhausted,
            rewindCommandError != null, !busy && !saving && recorder?.CanAcceptSave == true, saving, busy);
        SaveButton.IsEnabled = rewindPresentation.SaveState == RewindSaveState.Available;
        RewindOnButton.IsEnabled = RewindOffButton.IsEnabled = !busy && !saving && !micTestStarting && !conventionalStarting;
        UpdateScreenshotPresentation();
        UpdateMicrophonePresentation();
        DurationText.Text = $"{settings.BufferSeconds} сек";
        DurationText.ToolTip = running ? $"Доступно {recorder!.AvailableSeconds:F0} из {settings.BufferSeconds} сек" : "Длительность Rewind из настроек";
        var active = rewindPresentation.State == RewindState.On;
        var fill = (System.Windows.Media.Brush)FindResource(active ? "RewindActive" : "MainRewindInactive");
        BufferStateDot.Fill = ExpandedBufferStateDot.Fill = fill;
        var glow = active ? new System.Windows.Media.Effects.DropShadowEffect
            { Color = System.Windows.Media.Color.FromRgb(136,255,0), BlurRadius = 6.2, ShadowDepth = 0, Opacity = 1 } : null;
        BufferStateDot.Effect = ExpandedBufferStateDot.Effect = glow;
        var state = rewindPresentation.State switch
        {
            RewindState.Starting => "Запускаю Rewind…",
            RewindState.Stopping => "Останавливаю Rewind…",
            RewindState.Recovering => "Восстанавливаю Rewind…",
            RewindState.Error => rewindCommandError ?? "Захват прерван. Выключи и включи Rewind заново.",
            RewindState.On => "Буфер Rewind включён",
            _ => "Буфер Rewind выключен"
        };
        RewindStatusButton.ToolTip = ExpandedRewindStatusButton.ToolTip = state;
        System.Windows.Automation.AutomationProperties.SetName(RewindStatusButton, state + ". Выбрать состояние");
        System.Windows.Automation.AutomationProperties.SetName(ExpandedRewindStatusButton, state + ". Закрыть выбор");
        System.Windows.Automation.AutomationProperties.SetItemStatus(RewindStatusButton, state);
        RewindOnButton.Opacity = active ? .56 : 1;
        RewindOffButton.Opacity = rewindPresentation.State == RewindState.Off ? .56 : 1;
        MainStateText.Text = rewindPresentation.State is RewindState.Starting or RewindState.Stopping or RewindState.Recovering or RewindState.Error
            ? state : saving ? "Принимаю клип…" : recorder?.OutstandingSaves > 0 ? $"{(recorder.ProcessingSaves > 0 ? "Сохранение клипа" : "Подготовка клипа")} · в очереди {Math.Max(0, recorder.OutstandingSaves - recorder.ProcessingSaves - recorder.PreparingSaves)}" : running && !SaveButton.IsEnabled ? "Набираю буфер Rewind…" : "";
        SaveButton.ToolTip = saving ? "Принимаю клип…" : recorder?.SaveQueueFull == true ? "Очередь Rewind заполнена; дождись завершения экспорта" : SaveButton.IsEnabled
            ? "Сохранить последние секунды Rewind" : "Сохранение доступно после появления завершённых фрагментов буфера";
        UpdateRecordingPresentation();
        ObserveRewindOverlay();
    }

    private void RewindStatus_Click(object sender, RoutedEventArgs e)
    {
        RewindSelector.Visibility = RewindSelector.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        RewindStatusButton.Visibility = RewindSelector.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        if (RewindSelector.Visibility == Visibility.Visible) ExpandedRewindStatusButton.Focus();
    }
    private void CloseRewindSelector()
    {
        if (RewindSelector == null) return;
        RewindSelector.Visibility = Visibility.Collapsed;
        RewindStatusButton.Visibility = Visibility.Visible;
    }
    private void MainWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RewindSelector.Visibility == Visibility.Visible && !RewindSelector.IsMouseOver && !RewindStatusButton.IsMouseOver)
            CloseRewindSelector();
    }
    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape || RewindSelector.Visibility != Visibility.Visible) return;
        CloseRewindSelector(); RewindStatusButton.Focus(); e.Handled = true;
    }
    private async void RewindOn_Click(object sender, RoutedEventArgs e) { CloseRewindSelector(); await SetRewindEnabled(true); }
    private async void RewindOff_Click(object sender, RoutedEventArgs e) { CloseRewindSelector(); await SetRewindEnabled(false); }
    private static string SelectedTag(System.Windows.Controls.ComboBox box) => ((ComboBoxItem)box.SelectedItem).Tag.ToString()!;
    private static void Select(System.Windows.Controls.ComboBox box, object value) => box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(x => x.Tag.ToString() == value.ToString()) ?? box.Items[0];
    private void LoadSettings()
    {
        Select(CaptureMode, settings.CaptureMode); Select(AudioMode, settings.AudioMode); Select(CodecPicker, settings.Codec);
        Select(MicChannelPicker, settings.MicrophoneChannelMode);
        Select(HeightPicker, settings.Height); Select(FpsPicker, settings.Fps);
        // Preserve valid legacy/manual CQ values instead of silently selecting the first preset.
        foreach (var item in QualityPicker.Items.Cast<ComboBoxItem>().Where(x => x.Tag.ToString() is not ("23" or "27" or "30")).ToArray())
            QualityPicker.Items.Remove(item);
        if (settings.Quality is not (23 or 27 or 30))
            QualityPicker.Items.Add(new ComboBoxItem { Content = $"Сохранённое (CQ {settings.Quality})", Tag = settings.Quality.ToString() });
        Select(QualityPicker, settings.Quality);
        DurationBox.Text = settings.BufferSeconds.ToString(); BitrateBox.Text = settings.MaxBitrateMbps.ToString(); FolderBox.Text = settings.OutputDirectory;
        BufferLimitBox.Text = settings.MaxBufferMegabytes.ToString(); ThresholdBox.Text = settings.SilenceDb.ToString(); OffsetBox.Text = settings.AudioOffsetMs.ToString();
        BufferFolderBox.Text = settings.BufferDirectory;
        GroupByApplicationCheck.IsChecked = settings.GroupByApplication;
        DiscordCheck.IsChecked = settings.Discord; SoundCheck.IsChecked = settings.Sounds;
        HideBorderCheck.IsChecked = settings.HideDisplayBorder; CompressCheck.IsChecked = settings.CompressAfterSave;
        CompressionProfileText.Text = settings.Compression.Summary;
        RefreshHotkeyControls();
    }
    private void RefreshSources()
    {
        var selected = GamePicker.SelectedItem as GameWindow;
        GamePicker.Items.Clear(); GamePicker.Items.Add("Авто — последнее активное приложение");
        var windows = Native.Windows(); foreach (var window in windows) GamePicker.Items.Add(window);
        GamePicker.SelectedItem = windows.FirstOrDefault(w => w.Exe == (selected?.Exe ?? settings.GameExe));
        if (GamePicker.SelectedIndex < 0) GamePicker.SelectedIndex = 0;
        var displays = Native.Displays(); MonitorPicker.ItemsSource = displays;
        MonitorPicker.SelectedItem = displays.FirstOrDefault(x => x.Device == settings.MonitorDevice) ?? displays.FirstOrDefault(x => x.Primary) ?? displays.FirstOrDefault();
        var microphones = new List<MicrophoneChoice> { new("default", "По умолчанию (связь)"), new("off", "Не записывать микрофон") };
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) using (device) microphones.Add(new(device.ID, device.FriendlyName));
        MicPicker.ItemsSource = microphones; MicPicker.SelectedItem = microphones.FirstOrDefault(x => x.Id == settings.MicrophoneId) ?? microphones[0];
    }
    private void ReadSettings()
    {
        SaveAudioControls();
        // Validate a copy so invalid text cannot mutate a running session.
        var next = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        next.CaptureMode = SelectedTag(CaptureMode); next.AudioMode = SelectedTag(AudioMode); next.Codec = SelectedTag(CodecPicker);
        next.Height = int.Parse(SelectedTag(HeightPicker)); next.Fps = int.Parse(SelectedTag(FpsPicker)); next.Quality = int.Parse(SelectedTag(QualityPicker));
        next.BufferSeconds = int.Parse(DurationBox.Text); next.MaxBitrateMbps = int.Parse(BitrateBox.Text); next.OutputDirectory = FolderBox.Text.Trim();
        next.MaxBufferMegabytes = int.Parse(BufferLimitBox.Text); next.SilenceDb = double.Parse(ThresholdBox.Text); next.AudioOffsetMs = int.Parse(OffsetBox.Text);
        if (!double.IsFinite(next.SilenceDb)) throw new ArgumentException("Порог тишины: от −90 до −10 dBFS.");
        next.BufferDirectory = BufferFolderBox.Text.Trim();
        next.GroupByApplication = GroupByApplicationCheck.IsChecked == true;
        next.MicrophoneId = ((MicrophoneChoice)MicPicker.SelectedItem).Id; next.Discord = DiscordCheck.IsChecked == true; next.Sounds = SoundCheck.IsChecked == true;
        next.MicrophoneChannelMode = SelectedTag(MicChannelPicker);
        next.HideDisplayBorder = HideBorderCheck.IsChecked == true;
        next.MonitorDevice = (MonitorPicker.SelectedItem as Display)?.Device ?? "";
        next.GameExe = (GamePicker.SelectedItem as GameWindow)?.Exe ?? "";
        if (next.GameExe.Length > 0)
        {
            var name = GameNameBox.Text.Trim();
            if (name.Length == 0) next.GameNames.Remove(next.GameExe);
            else next.GameNames[next.GameExe] = name;
        }
        next.Validate(); settings = next; settings.Save(); RegisterKeys();
    }
    private Task Toggle() => SetRewindEnabled(!recordingWanted);
    private async Task SetRewindEnabled(bool enabled)
    {
        // Repeated ON/OFF must not restart a producer or accidentally toggle it.
        if (enabled ? recordingWanted : !recordingWanted) return;
        if (busy || saving || micTestStarting || conventionalStarting) return;
        busy = true;
        rewindCommandError = null;
        rewindTransition = enabled ? RewindState.Starting : RewindState.Stopping;
        UpdateMainPresentation();
        try
        {
            if (!enabled)
            {
                recordingWanted = false; recovery.Reset();
                if (recorder != null)
                {
                    if (recorder.Recording.State is RecordingState.Recording or RecordingState.Stopping or RecordingState.Finalizing)
                        await recorder.SetRewindEnabled(false);
                    else { await recorder.DisposeAsync(); recorder = null; }
                }
                SetCaptureSettingsEnabled(recorder == null); SaveButton.IsEnabled = false;
                StatusText.Text = "●  Запись выключена"; DetailText.Text = "Буфер очищен. Сохранённые клипы остаются."; WarningText.Text = "";
                tray.Icon = System.Drawing.SystemIcons.Application;
            }
            else
            {
                await StopMicrophoneTest();
                if (recorder?.Running == true && recorder.Recording.State is RecordingState.Recording or RecordingState.Stopping or RecordingState.Finalizing)
                {
                    await recorder.SetRewindEnabled(true); recordingWanted = true; recovery.Reset(); healthySince = Native.Clock;
                    SetCaptureSettingsEnabled(false); return;
                }
                if (recorder != null) { await recorder.DisposeAsync(); recorder = null; }
                ReadSettings();
                var game = CaptureSource.ResolveGame(GamePicker.SelectedItem as GameWindow, foregroundGame);
                var display = MonitorPicker.SelectedItem as Display ?? throw new InvalidOperationException("Нет доступного монитора.");
                StatusText.Text = "●  Подготовка записи…"; WarningText.Text = "";
                recorder = new Recorder(settings, game, display, microphoneMute);
                await recorder.Start();
                UpdateCaptureMicrophoneGain();
                recordingWanted = true; recovery.Reset(); healthySince = Native.Clock;
                SetCaptureSettingsEnabled(false);
                tray.Icon = System.Drawing.SystemIcons.Information;
            }
        }
        catch (Exception ex) { rewindCommandError = ex.Message; StatusText.Text = "●  Ошибка Rewind"; WarningText.Text = ex.Message; Notify("Ошибка Rewind", ex.Message, true); }
        finally { busy = false; rewindTransition = null; UpdateMainPresentation(); }
    }
    private async Task SaveClip(long? receivedTick = null, string commandSource = "button")
    {
        receivedTick ??= Stopwatch.GetTimestamp();
        if (recovering) { saveAfterRecovery = true; HotkeyDiagnostics.Mark("save-deferred", new { Reason = "capture-recovery" }); return; }
        if (exiting || recorder == null || saving || busy)
        {
            HotkeyDiagnostics.Mark("save-rejected", new { exiting, NoRecorder = recorder == null, saving, busy });
            if (commandSource == "hotkey" && !exiting)
            {
                var reason = recorder == null ? "Сначала включи Rewind. Актуальные сочетания — в настройках горячих клавиш." : "Rewind занят запуском или приёмом клипа. Повтори попытку.";
                LastClipText.Text = "Клип не принят: " + reason;
                Notify("Клип не принят", reason, true);
            }
            return;
        }
        var saveRecorder = recorder;
        var commandDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        saveCommands.Add(commandDone.Task);
        var saveOperation = Guid.NewGuid();
        using var timing = SaveTiming.Begin(saveOperation, receivedTick);
        timing?.MarkAt("save-command-received", receivedTick ?? Stopwatch.GetTimestamp(), new { Source = commandSource });
        timing?.Mark("command-accepted");
        saving = true;
        bool acceptedRequest = false;
        Task started = Task.CompletedTask;
        try
        {
            var acceptance = saveRecorder.AcceptSave(); // Freeze command QPC before presentation work.
            UpdateMainPresentation();
            var saveMonitor = OverlayTarget();
            var accepted = await acceptance;
            HotkeyDiagnostics.Mark("save-accepted", new { Stage = accepted.Stage.ToString() });
            latestSaveOperation = saveOperation;
            LastClipText.Text = accepted.Stage == SaveClipStage.Queued ? "Клип в очереди" : accepted.Stage == SaveClipStage.Exporting ? "Сохранение клипа" : "Подготовка клипа";
            overlays?.BeginRewindSave(saveOperation, saveMonitor, stage: accepted.Stage);
            acceptedRequest = true;
            saving = false;
            UpdateMainPresentation();
            started = ObserveQueuedSave(accepted, saveOperation);
            var clip = await accepted.Completion;
            HotkeyDiagnostics.Mark("save-published", new { clip.Path, clip.Duration });
            timing?.Mark("save-returned", new { clip.Path, clip.Duration });
            LastClipText.Text = $"Сохранено {clip.Duration:F0} с · {Path.GetFileName(clip.Path)}";
            overlays?.CompleteRewindSave(saveOperation, clip);
            if (settings.CompressAfterSave)
            {
                try
                {
                    var jobId = compression.Enqueue(clip.Path, settings.Compression);
                    overlayCompressionJobs.Add(jobId, (saveOperation, saveMonitor));
                }
                catch (Exception ex) { ClipFiles.Log("compression-enqueue", ex, clip.Path); Notify("Клип сохранён, но не добавлен в сжатие", ex.Message, true); }
            }
            if (clip.Warnings.Count > 0) Notify("Клип сохранён с предупреждением", string.Join("\n", clip.Warnings), true);
            if (settings.Sounds) { if (clip.Warnings.Count > 0) SystemSounds.Exclamation.Play(); else SystemSounds.Asterisk.Play(); }
        }
        catch (Exception ex) { HotkeyDiagnostics.Mark("save-failed", new { ex.Message }); timing?.Mark("save-failed", new { ex.Message }); overlays?.FailRewindSave(saveOperation); LastClipText.Text = "Клип не сохранён: " + ex.Message; Notify("Ошибка сохранения", ex.Message, true); if (settings.Sounds) SystemSounds.Hand.Play(); }
        finally { await started; if (!acceptedRequest) saving = false; saveCommands.Remove(commandDone.Task); commandDone.TrySetResult(); UpdateMainPresentation(); }
    }
    private async Task ObserveQueuedSave(AcceptedSave accepted, Guid operation)
    {
        if (await accepted.PreparationStarted && accepted.Stage == SaveClipStage.Preparing && !exiting)
        {
            overlays?.PrepareRewindSave(operation);
            if (latestSaveOperation == operation) LastClipText.Text = "Подготовка клипа";
            UpdateMainPresentation();
        }
        if (await accepted.ExportStarted && !exiting)
        {
            overlays?.ExportRewindSave(operation);
            if (latestSaveOperation == operation) LastClipText.Text = "Сохранение клипа";
            UpdateMainPresentation();
        }
    }
    private async Task Tick()
    {
        if (ticking || exiting) return;
        ticking = true;
        try
        {
            CompressionStatusText.Text = compression.Status;
            if (recorder?.Running != true)
            {
                var hwnd = Native.GetForegroundWindow();
                var active = Native.Windows().FirstOrDefault(x => x.Handle == hwnd);
                // Remember the capture candidate across switching back to Replay. Explicit picker always wins.
                if (active != null && active.Exe is not ("chrome" or "msedge" or "firefox" or "Discord" or "Codex" or "WindowsTerminal" or "powershell" or "pwsh")) foregroundGame = active;
            }
            if (recorder == null) { tray.Text = "Rewind — выключен"; return; }
            if (recordingWanted && recorder.RecoverableCaptureLoss && !busy && !saving && recovery.CanAttempt(Native.Clock))
            { await RecoverCapture(); return; }
            if (recorder.Running && Native.Clock - healthySince > 60) recovery.Reset();
            await recorder.Maintain();
            StatusText.Text = recorder.Running ? $"●  Запись · {recorder.AvailableSeconds:F0} / {settings.BufferSeconds} с" : "●  Запись остановлена";
            DetailText.Text = $"{recorder.GameName} · {recorder.Encoder.Replace("_nvenc", "").ToUpperInvariant()} · {settings.Height}p / {settings.Fps} FPS · {recorder.CaptureBackend}";
            var warning = recovery.Exhausted && recorder.RecoverableCaptureLoss
                ? "Не удалось восстановить захват после 3 попыток. Выключи и включи буфер; при повторении отключи режим без рамки."
                : recorder.Warning ?? hotkeyWarning; WarningText.Text = warning ?? "";
            tray.Text = ConventionalRecording.State switch
            {
                RecordingState.Recording => "Rewind — запись " + RecordingPresentation.FormatDuration(ConventionalRecording.Duration),
                RecordingState.Stopping or RecordingState.Finalizing => "Rewind — сохранение записи",
                _ => recordingWanted && recorder.Running ? $"Rewind — буфер {recorder.AvailableSeconds:F0} с"
                    : recorder.Running ? "Rewind — буфер выключен" : "Rewind — запись остановлена"
            };
            tray.Icon = warning != null ? System.Drawing.SystemIcons.Warning : recorder.Running ? System.Drawing.SystemIcons.Information : System.Drawing.SystemIcons.Application;
            var warningKind = warning == null ? null : System.Text.RegularExpressions.Regex.Replace(warning, @"\d+", "#");
            if (!(recorder.RecoverableCaptureLoss && !recovery.Exhausted) && warning != null && warningKind != lastWarning && (DateTime.Now - lastNotice).TotalSeconds > 15) { Notify("Проверь запись", warning, true); lastNotice = DateTime.Now; }
            lastWarning = warningKind;
            if (!recorder.Running) SetCaptureSettingsEnabled(!recordingWanted && ConventionalRecording.State is not (RecordingState.Starting or RecordingState.Stopping or RecordingState.Finalizing));
        }
        catch (Exception ex) { WarningText.Text = ex.Message; }
        finally { ticking = false; UpdateMainPresentation(); }
    }
    private async Task RecoverCapture()
    {
        var failed = recorder!;
        recovering = true;
        busy = true;
        recovery.Attempt(Native.Clock);
        StatusText.Text = $"●  Восстановление захвата · попытка {recovery.Attempts}/3";
        WarningText.Text = "Есть разрыв записи. Повторно подключаю тот же монитор без смены режима захвата.";
        UpdateMainPresentation();
        try
        {
            await failed.RestartCapture();
            recovery.Reset(); healthySince = Native.Clock;
            SetCaptureSettingsEnabled(false);
        }
        catch (Exception ex)
        {
            ClipFiles.Log("capture-recovery", ex);
            WarningText.Text = recovery.Exhausted ? "Захват не восстановлен после 3 попыток. Выключи режим без рамки и включи буфер заново." : "Монитор пока недоступен. Следующая попытка через несколько секунд.";
        }
        finally
        {
            recovering = false;
            busy = false;
            UpdateMainPresentation();
        }
        if (saveAfterRecovery) { saveAfterRecovery = false; await SaveClip(); }
    }
    private void Notify(string title, string body, bool warning) { HotkeyDiagnostics.Mark("notification", new { title, body, warning }); tray.ShowBalloonTip(5000, title, body.Length > 240 ? body[..240] + "…" : body, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info); }
    private void ShowWindow() { RegisterKeys(); UpdateMainPresentation(); Show(); WindowState = WindowState.Normal; Activate(); }
    private void OpenFolder() { Directory.CreateDirectory(settings.OutputDirectory); Process.Start(new ProcessStartInfo(settings.OutputDirectory) { UseShellExecute = true }); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSources();
    private void CompressionChanged(object sender, RoutedEventArgs e)
    { settings.CompressAfterSave = CompressCheck.IsChecked == true; settings.Save(); }
    private void ImportCompression_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog { Title = "Настройки MP4 Compressor", Filter = "Настройки JSON|*.json", FileName = "settings.json", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "prjcts", "MP4 COMPRESSOR") };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try { settings.Compression = CompressionProfile.Import(dialog.FileName); settings.Save(); CompressionProfileText.Text = settings.Compression.Summary; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось импортировать профиль"); }
    }
    private void Browse_Click(object sender, RoutedEventArgs e) { using var dialog = new Forms.FolderBrowserDialog { InitialDirectory = FolderBox.Text }; if (dialog.ShowDialog() == Forms.DialogResult.OK) FolderBox.Text = dialog.SelectedPath; }
    private void BrowseBuffer_Click(object sender, RoutedEventArgs e) { using var dialog = new Forms.FolderBrowserDialog { InitialDirectory = BufferFolderBox.Text }; if (dialog.ShowDialog() == Forms.DialogResult.OK) BufferFolderBox.Text = dialog.SelectedPath; }
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveClip();
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; CloseRewindSelector(); _ = StopMicrophoneTest(); Hide(); } }
    private async Task Exit()
    {
        if (exiting) return;
        if (saving || busy) { Notify("Rewind занят", "Дождись завершения сохранения или запуска записи.", true); return; }
        exiting = true; timer.Stop();
        DetachHotkeys();
        if (overlays != null) await overlays.DisposeAsync();
        screenshotCancellation?.Cancel();
        if (screenshotOperation != null) { try { await screenshotOperation; } catch { /* The screenshot command reports local errors. */ } }
        meterTimer.Stop(); audioSaveTimer.Stop(); SaveAudioControls(); await StopMicrophoneTest();
        await StopRecording();
        if (recorder != null) await recorder.DisposeAsync();
        await Task.WhenAll(saveCommands.ToArray());
        await compression.DisposeAsync();
        overlayCompressionJobs.Clear();
        tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); Application.Current.Shutdown();
    }
    private void RegisterKeys()
    {
        if (source == null) return;
        Dispatcher.VerifyAccess();
        var previousWarning = hotkeyWarning;
        HotkeyDiagnostics.Mark("configuration", new { Hwnd = source.Handle.ToInt64(), IsVisible, WindowState, Bindings = Enum.GetValues<HotkeyAction>().Select(a =>
            new { Action = a.ToString(), HotkeyValue(settings, a).Enabled, HotkeyValue(settings, a).Modifiers, HotkeyValue(settings, a).Key }).ToArray() });
        foreach (var action in Enum.GetValues<HotkeyAction>()) RegisterConfiguredHotkey(action);
        RefreshHotkeyControls();
        UpdateHotkeyWarnings();
        if (hotkeyWarning != null && hotkeyWarning != previousWarning)
        {
            var warning = hotkeyWarning;
            Dispatcher.BeginInvoke(() => { if (!exiting && hotkeyWarning == warning) Notify("Горячие клавиши недоступны", "Открой Настройки → Горячие клавиши: выбери свободные сочетания или повтори регистрацию после освобождения сочетаний.\n" + warning, true); });
        }
    }
    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        var messageReceived = msg == 0x0312 ? Stopwatch.GetTimestamp() : 0;
        // Caption clicks are outside the selector too; leave native window handling intact.
        if (msg == 0x00A1 /* WM_NCLBUTTONDOWN */) CloseRewindSelector();
        if (msg == 0x0312)
        {
            handled = true;
            HotkeyDiagnostics.Mark("message", new { Hwnd = hwnd.ToInt64(), Id = wParam.ToInt64(), Raw = lParam.ToInt64(), IsVisible, IsActive, WindowState, Foreground = Native.GetForegroundWindow().ToInt64() });
            // Ignore stale queued messages from a shortcut that was replaced or disabled.
            var action = Enum.GetValues<HotkeyAction>().Cast<HotkeyAction?>().FirstOrDefault(a =>
                Registration(a!.Value) is { } registration && wParam == registration.Id &&
                registration.Modifiers == ((uint)(long)lParam & 0xFFFF) && registration.Key == (((uint)(long)lParam >> 16) & 0xFFFF));
            if (editingHotkey != null && action != null)
                CaptureHotkey(HotkeyText((uint)(long)lParam & 0xFFFF, ((uint)(long)lParam >> 16) & 0xFF));
            else if (editingHotkey == null && action != null && !exiting)
                Dispatcher.BeginInvoke(() => _ = ExecuteHotkey(action.Value, messageReceived));
            else HotkeyDiagnostics.Mark("message-ignored", new { Action = action?.ToString(), Editing = editingHotkey != null, exiting });
        }
        return 0;
    }
    public static (uint Modifiers, uint Key) ParseHotkey(string text)
    {
        uint modifiers = 0, key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            switch (part.ToUpperInvariant())
            {
                case "CTRL": modifiers |= 2; break; case "ALT": modifiers |= 1; break; case "SHIFT": modifiers |= 4; break;
                default: if (key != 0 || !Enum.TryParse<Forms.Keys>(part, true, out var parsed) || (uint)parsed > 255 || (uint)parsed < 0x20) throw new ArgumentException("Пример хоткея: Ctrl+Alt+F10"); key = (uint)parsed; break;
            }
        ValidateHotkey(modifiers, key);
        return (modifiers, key);
    }
    private static string HotkeyText(uint modifiers, uint key) => key == 0 ? "—" : ((modifiers & 2) != 0 ? "Ctrl+" : "") + ((modifiers & 1) != 0 ? "Alt+" : "") + ((modifiers & 4) != 0 ? "Shift+" : "") + ((Forms.Keys)key);
}


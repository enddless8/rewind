using System.Windows;
using Brushes = System.Windows.Media.Brushes;
using System.Windows.Threading;

namespace Replay;
public partial class MainWindow
{
    private AudioTrack? microphoneTest;
    private MicrophoneMonitor? microphoneMonitor;
    private Task microphoneTestStopping = Task.CompletedTask;
    private int microphoneTestGeneration;
    private bool captureSettingsEnabled = true;
    private bool audioControlsReady;
    private bool micTestStarting;
    private int microphoneCapabilityGeneration;
    private readonly SemaphoreSlim microphoneCapabilityGate = new(1, 1);
    private MicrophoneChannelCapability microphoneCapability = MicrophoneChannelCapability.Unknown;
    private readonly DispatcherTimer meterTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer audioSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private void InitializeAudioControls()
    {
        SystemVolumeSlider.Value = settings.SystemVolume;
        MicVolumeSlider.Value = settings.MicrophoneVolume;
        MicBoostSlider.Value = settings.MicrophoneBoostDb;
        audioControlsReady = true;
        audioSaveTimer.Tick += (_, _) => { audioSaveTimer.Stop(); SaveAudioControls(); };
        meterTimer.Tick += (_, _) =>
        {
            var track = microphoneTest ?? recorder?.Microphone;
            var fresh = track != null && Native.Clock - track.LastPacket < .2;
            var peak = fresh ? track!.PeakDb : -120;
            MicMeter.Value = Math.Clamp(60 + peak, 0, 60);
            if (microphoneTest == null) return;
            if (!micTestStarting && (microphoneMonitor?.Error ?? microphoneTest.Health) is { } error)
            {
                _ = StopMicrophoneTest(); MicTestText.Text = error; return;
            }
            TestMicMeter.Value = Math.Clamp(60 + peak, 0, 60);
            TestMicMeter.Foreground = fresh && track!.Clipping ? Brushes.OrangeRed : Brushes.MediumAquamarine;
            MicTestText.Text = track!.Health ?? (fresh && track.Clipping ? "Перегрузка! Уменьши усиление или громкость." : fresh ? $"Пик {peak:F1} dBFS · RMS {track.RmsDb:F1} dBFS" : "Ожидаю сигнал микрофона…");
        };
        meterTimer.Start();
        MicPicker.SelectionChanged += (_, _) => { _ = StopMicrophoneTest(); UpdateMicrophonePresentation(); _ = RefreshMicrophoneChannels(); };
        LegacyAudioFields.IsVisibleChanged += (_, _) => { if (!LegacyAudioFields.IsVisible) _ = StopMicrophoneTest(); else if (audioControlsReady) _ = RefreshMicrophoneChannels(); };
        UpdateMicrophoneTestAvailability();
        _ = RefreshMicrophoneChannels();
    }
    private void AudioSettingsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!audioControlsReady) return;
        if (microphoneTest != null) microphoneTest.Gain = MicVolumeSlider.Value / 100 * Math.Pow(10, MicBoostSlider.Value / 20);
        UpdateCaptureMicrophoneGain();
        audioSaveTimer.Stop(); audioSaveTimer.Start();
    }
    private void SaveAudioControls()
    {
        settings.SystemVolume = SystemVolumeSlider.Value;
        settings.MicrophoneVolume = MicVolumeSlider.Value;
        settings.MicrophoneBoostDb = MicBoostSlider.Value;
        UpdateCaptureMicrophoneGain();
        try { settings.Save(); }
        catch (Exception ex) { WarningText.Text = "Не удалось сохранить громкость: " + ex.Message; }
    }
    private async void MicTest_Click(object sender, RoutedEventArgs e)
    {
        if (microphoneTest != null || micTestStarting) { await StopMicrophoneTest(); return; }
        if (MicPicker.SelectedItem is not MicrophoneChoice choice || choice.Id == "off") { MicTestText.Text = "Сначала выбери микрофон."; return; }
        if (!captureSettingsEnabled || exiting || busy || recorder?.Running == true || conventionalStarting) return;
        var generation = ++microphoneTestGeneration;
        micTestStarting = true;
        UpdateMicrophoneTestAvailability();
        UpdateMainPresentation();
        try
        {
            MicTestText.Text = "Подключаю микрофон и воспроизведение…";
            await microphoneTestStopping;
            if (generation != microphoneTestGeneration) return;
            var monitor = new MicrophoneMonitor(choice.Id, MicVolumeSlider.Value / 100 * Math.Pow(10, MicBoostSlider.Value / 20), settings.SilenceDb, SelectedTag(MicChannelPicker));
            microphoneMonitor = monitor; microphoneTest = monitor.Input;
            await monitor.StartAsync();
            if (generation == microphoneTestGeneration)
            {
                if (monitor.Input.InputChannelCapability is { } capability) SetMicrophoneChannelCapability(capability);
                MicTestButton.Content = "Остановить проверку";
            }
        }
        catch (Exception ex)
        {
            if (generation == microphoneTestGeneration)
            {
                var error = microphoneMonitor?.Error ?? "Проверка микрофона недоступна: " + ex.Message;
                await StopMicrophoneTest(); MicTestText.Text = error;
            }
        }
        finally
        {
            if (generation == microphoneTestGeneration) { micTestStarting = false; UpdateMicrophoneTestAvailability(); UpdateMainPresentation(); }
        }
    }
    private Task StopMicrophoneTest()
    {
        ++microphoneTestGeneration;
        var monitor = microphoneMonitor; var track = microphoneTest;
        microphoneMonitor = null; microphoneTest = null; micTestStarting = false;
        var stopping = monitor != null ? monitor.DisposeAsync().AsTask() : track?.StopPreviewAsync() ?? Task.CompletedTask;
        microphoneTestStopping = Task.WhenAll(microphoneTestStopping, stopping);
        TestMicMeter.Value = 0; MicTestButton.Content = "Проверить микрофон";
        MicTestText.Text = "Живой уровень и прослушивание без записи.";
        UpdateMicrophoneTestAvailability(); UpdateMainPresentation();
        return microphoneTestStopping;
    }
    private void UpdateMicrophoneTestAvailability()
    {
        MicPicker.IsEnabled = captureSettingsEnabled && !micTestStarting && !exiting;
        MicChannelPicker.IsEnabled = MicPicker.IsEnabled && microphoneCapability.ShowMode;
        MicTestButton.IsEnabled = MicPicker.IsEnabled && MicPicker.SelectedItem is MicrophoneChoice { Id: not "off" };
    }
    private async Task RefreshMicrophoneChannels()
    {
        var generation = ++microphoneCapabilityGeneration;
        var id = (MicPicker.SelectedItem as MicrophoneChoice)?.Id ?? "off";
        SetMicrophoneChannelCapability(MicrophoneChannelCapability.Unknown);
        await microphoneCapabilityGate.WaitAsync();
        try
        {
            if (generation != microphoneCapabilityGeneration || exiting) return;
            var capability = await Task.Run(() => MicrophoneChannels.Inspect(id));
            if (generation == microphoneCapabilityGeneration && !exiting) SetMicrophoneChannelCapability(capability);
        }
        finally { microphoneCapabilityGate.Release(); }
    }
    private void SetMicrophoneChannelCapability(MicrophoneChannelCapability capability)
    {
        microphoneCapability = capability;
        MicChannelRow.Visibility = capability.ShowMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateMicrophoneChannelFeedback();
        UpdateMicrophoneTestAvailability();
    }
    private void UpdateMicrophoneChannelFeedback()
    {
        var preference = MicChannelPicker.SelectedItem == null ? settings.MicrophoneChannelMode : SelectedTag(MicChannelPicker);
        var feedback = (MicPicker.SelectedItem as MicrophoneChoice)?.Id == "off" ? null : microphoneCapability.Feedback(preference);
        MicChannelFeedback.Text = feedback ?? "";
        MicChannelFeedback.Visibility = feedback == null ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void MicrophoneChannelMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!audioControlsReady || MicChannelPicker.SelectedItem == null) return;
        if (!captureSettingsEnabled || micTestStarting || exiting)
        { Select(MicChannelPicker, settings.MicrophoneChannelMode); return; }
        settings.MicrophoneChannelMode = SelectedTag(MicChannelPicker);
        UpdateMicrophoneChannelFeedback();
        try { settings.Save(); }
        catch (Exception ex) { WarningText.Text = "Не удалось сохранить режим микрофона: " + ex.Message; }
        await StopMicrophoneTest();
    }
    private void UpdateCaptureMicrophoneGain()
    {
        if (recorder?.Microphone is { } track)
            track.Gain = MicVolumeSlider.Value / 100 * Math.Pow(10, MicBoostSlider.Value / 20);
    }
    private void SetMicrophoneMuted(bool muted)
    {
        if (GetMicrophoneRuntimeState() == MicrophoneRuntimeState.Unavailable || microphoneMute.Muted == muted) return;
        microphoneMute.SetMuted(muted);
        UpdateMicrophonePresentation();
        if (muted) overlays?.ShowMicrophoneMuted(); else overlays?.ShowMicrophoneEnabled();
    }
    private void ToggleMicrophoneMuted() => SetMicrophoneMuted(!microphoneMute.Muted);
    private void MicrophoneQuick_Click(object sender, RoutedEventArgs e) => ToggleMicrophoneMuted();
    private MicrophoneRuntimeState GetMicrophoneRuntimeState()
    {
        var configured = recorder != null ? settings.MicrophoneId : (MicPicker.SelectedItem as MicrophoneChoice)?.Id ?? settings.MicrophoneId;
        if (configured == "off") return MicrophoneRuntimeState.Unavailable;
        if (recorder != null && rewindTransition != RewindState.Starting &&
            (recorder.Microphone == null || recorder.Microphone.Health != null)) return MicrophoneRuntimeState.Unavailable;
        return microphoneMute.Muted ? MicrophoneRuntimeState.Muted : MicrophoneRuntimeState.Enabled;
    }
    private void UpdateMicrophonePresentation()
    {
        microphoneRuntimeState = GetMicrophoneRuntimeState();
        var available = microphoneRuntimeState != MicrophoneRuntimeState.Unavailable;
        MicrophoneQuickButton.IsEnabled = available;
        MicrophoneQuickButton.Opacity = available ? 1 : .4;
        MicrophoneQuickIcon.Source = (System.Windows.Media.ImageSource)FindResource(
            microphoneRuntimeState == MicrophoneRuntimeState.Muted ? "MainFigmaMicrophoneMutedIcon" : "MainFigmaMicrophoneIcon");
        var action = microphoneRuntimeState switch
        {
            MicrophoneRuntimeState.Muted => "Включить микрофон",
            MicrophoneRuntimeState.Enabled => "Выключить микрофон",
            _ => "Микрофон не записывается"
        };
        MicrophoneQuickButton.ToolTip = action;
        System.Windows.Automation.AutomationProperties.SetName(MicrophoneQuickButton, action);
        System.Windows.Automation.AutomationProperties.SetItemStatus(MicrophoneQuickButton,
            available ? microphoneMute.Muted ? "Микрофон выключен" : "Микрофон включён" : "Микрофон не записывается");
    }
    private void SetCaptureSettingsEnabled(bool enabled)
    {
        // Keep the audio page's layout. Only its two microphone-gain sliders are live.
        CaptureFields.IsEnabled = LegacyOutputFields.IsEnabled = AdvancedPanel.IsEnabled = enabled;
        // Hotkey registration changes no capture parameters and remains transactional.
        // Its editors must stay available while Rewind/recording lock the producer settings.
        captureSettingsEnabled = enabled;
        UpdateMicrophoneTestAvailability();
        AudioMode.IsEnabled = DiscordCheck.IsEnabled = SoundCheck.IsEnabled = SystemVolumeSlider.IsEnabled = enabled;
    }
}

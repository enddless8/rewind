using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Replay;

// One-way microphone preview. No loopback source, recorder, history, writer or settings owner.
internal sealed class MicrophoneMonitor : IAsyncDisposable, IMMNotificationClient
{
    internal const int PlaybackLatencyMilliseconds = 50;
    internal readonly MicrophoneMonitorBuffer Buffer = new();
    internal AudioTrack Input { get; }
    internal string? InputDeviceId { get; private set; }
    internal string? OutputDeviceId { get; private set; }
    internal string? OutputDeviceName { get; private set; }
    internal string? OutputFormat { get; private set; }
    internal string? Error => Volatile.Read(ref error);
    internal bool WorkersStopped => (outputWorker?.IsCompleted ?? true) && (cleanup?.IsCompleted ?? false);
    private readonly string selectedInput;
    private readonly CancellationTokenSource stop = new();
    private readonly object lifecycle = new();
    private Task? startup, outputWorker, cleanup;
    private int stopRequested;
    private string? error;

    internal MicrophoneMonitor(string microphoneId, double gain, double threshold, string channelMode = "Auto")
    {
        if (microphoneId == "off") throw new ArgumentException("Сначала выбери микрофон.");
        selectedInput = microphoneId;
        Input = new AudioTrack("Microphone preview", true, microphoneId, null, 0, threshold, null, Buffer.Offer) { Gain = gain, MicrophoneChannelPreference = channelMode };
    }
    internal Task StartAsync()
    {
        lock (lifecycle)
        {
            if (cleanup != null) throw new ObjectDisposedException(nameof(MicrophoneMonitor));
            return startup ??= StartCoreAsync();
        }
    }
    private async Task StartCoreAsync()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outputWorker = Task.Factory.StartNew(() => Render(ready), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { await Task.WhenAll(Input.Start(), ready.Task).WaitAsync(TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false); }
        catch { RequestStop(); throw; }
    }
    private void Render(TaskCompletionSource ready)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var input = selectedInput == "default" ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : devices.GetDevice(selectedInput);
            using var output = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (input.State != DeviceState.Active || input.DataFlow != DataFlow.Capture)
                throw new InvalidOperationException("Микрофон недоступен.");
            InputDeviceId = input.ID; OutputDeviceId = output.ID; OutputDeviceName = output.FriendlyName;
            devices.RegisterEndpointNotificationCallback(this);
            try
            {
                // Register before validating defaults so changes during setup are not missed.
                using var current = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (current.ID != OutputDeviceId) Fail("Устройство воспроизведения изменилось. Запусти проверку снова.");
                if (selectedInput == "default")
                {
                    using var currentInput = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    if (currentInput.ID != InputDeviceId) Fail("Микрофон по умолчанию изменился. Запусти проверку снова.");
                }
                stop.Token.ThrowIfCancellationRequested();
                using var player = new WasapiOut(output, AudioClientShareMode.Shared, true, PlaybackLatencyMilliseconds);
                player.PlaybackStopped += (_, e) =>
                {
                    if (!stop.IsCancellationRequested) Fail("Воспроизведение прервано" + (e.Exception == null ? "." : ": " + e.Exception.Message));
                };
                player.Init(Buffer); OutputFormat = player.OutputWaveFormat.ToString();
                player.Play(); ready.TrySetResult();
                while (!stop.Token.WaitHandle.WaitOne(100))
                {
                    if (Input.Health is { } health) Fail("Микрофон недоступен: " + health);
                }
                // Stop joins NAudio's render thread here, never on WPF or a device callback.
                player.Stop();
            }
            finally { devices.UnregisterEndpointNotificationCallback(this); }
        }
        catch (OperationCanceledException) { ready.TrySetCanceled(); }
        catch (Exception ex)
        {
            if (!stop.IsCancellationRequested) Fail("Не удалось запустить прослушивание: " + ex.Message);
            ready.TrySetException(ex);
        }
        finally { Buffer.Stop(); _ = DisposeAsync(); }
    }
    private void RequestStop() { Buffer.Stop(); if (Interlocked.Exchange(ref stopRequested, 1) == 0) stop.Cancel(); }
    private void Fail(string message) { Interlocked.CompareExchange(ref error, message, null); RequestStop(); }
    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (cleanup == null) { RequestStop(); cleanup = CleanupAsync(); }
            return new ValueTask(cleanup);
        }
    }
    private async Task CleanupAsync()
    {
        if (startup != null) { try { await startup.ConfigureAwait(false); } catch { } }
        await Input.StopPreviewAsync().ConfigureAwait(false);
        if (outputWorker != null) await outputWorker.ConfigureAwait(false);
        stop.Dispose();
    }
    // Device notifications signal only; resource teardown belongs to the owning workers.
    public void OnDeviceStateChanged(string id, DeviceState state)
    { if (state != DeviceState.Active) DeviceLost(id); }
    public void OnDeviceRemoved(string id) => DeviceLost(id);
    private void DeviceLost(string id)
    {
        if (id == InputDeviceId) Fail("Микрофон отключён. Проверка остановлена.");
        else if (id == OutputDeviceId) Fail("Устройство воспроизведения отключено. Проверка остановлена.");
    }
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia && id != OutputDeviceId)
            Fail("Устройство воспроизведения изменилось. Запусти проверку снова.");
        else if (selectedInput == "default" && flow == DataFlow.Capture && role == Role.Communications && id != InputDeviceId)
            Fail("Микрофон по умолчанию изменился. Запусти проверку снова.");
    }
    public void OnDeviceAdded(string id) { }
    public void OnPropertyValueChanged(string id, PropertyKey key) { }
}

// Bounded PCM ring: callbacks never wait for the other callback. Underflows are silence,
// overflow drops oldest frames, and stalled audio expires rather than playing much later.
internal sealed class MicrophoneMonitorBuffer : IWaveProvider
{
    internal const int CapacityMilliseconds = 100;
    private const double MaxAgeSeconds = .12;
    private readonly byte[] pcm = new byte[AudioTrack.Rate * AudioTrack.BytesPerFrame * CapacityMilliseconds / 1000];
    private readonly object gate = new();
    private int read, count, stopped;
    private double oldest;
    private long offered, rendered, dropped, underrun;
    public WaveFormat WaveFormat { get; } = new(AudioTrack.Rate, 16, AudioTrack.Channels);
    internal long OfferedBytes => Interlocked.Read(ref offered);
    internal long RenderedBytes => Interlocked.Read(ref rendered);
    internal long DroppedBytes => Interlocked.Read(ref dropped);
    internal long UnderrunBytes => Interlocked.Read(ref underrun);
    internal int BufferedBytes { get { lock (gate) return count; } }
    internal void Stop() => Volatile.Write(ref stopped, 1);
    internal void Offer(AudioPacket packet)
    {
        var length = packet.Data.Length - packet.Data.Length % AudioTrack.BytesPerFrame;
        if (Volatile.Read(ref stopped) != 0) return;
        if (!Monitor.TryEnter(gate)) { Interlocked.Add(ref dropped, length); return; }
        try
        {
            if (Volatile.Read(ref stopped) != 0) return;
            var now = Native.Clock;
            if (count != 0 && now - oldest > MaxAgeSeconds) { Interlocked.Add(ref dropped, count); count = 0; }
            var skip = Math.Max(0, length - pcm.Length); length -= skip;
            var overflow = Math.Max(0, count + length - pcm.Length);
            read = (read + overflow) % pcm.Length; count -= overflow;
            Interlocked.Add(ref dropped, skip + overflow);
            if (count == 0 || overflow != 0) oldest = now;
            var at = (read + count) % pcm.Length;
            var first = Math.Min(length, pcm.Length - at);
            packet.Data.AsSpan(skip, first).CopyTo(pcm.AsSpan(at));
            packet.Data.AsSpan(skip + first, length - first).CopyTo(pcm);
            count += length; Interlocked.Add(ref offered, length);
        }
        finally { Monitor.Exit(gate); }
    }
    public int Read(byte[] buffer, int offset, int requested)
    {
        Array.Clear(buffer, offset, requested);
        if (Volatile.Read(ref stopped) != 0 || !Monitor.TryEnter(gate)) return requested;
        try
        {
            if (Volatile.Read(ref stopped) != 0 || (count != 0 && Native.Clock - oldest > MaxAgeSeconds))
            { Interlocked.Add(ref dropped, count); count = 0; }
            var length = Math.Min(count, requested - requested % AudioTrack.BytesPerFrame);
            var first = Math.Min(length, pcm.Length - read);
            pcm.AsSpan(read, first).CopyTo(buffer.AsSpan(offset));
            pcm.AsSpan(0, length - first).CopyTo(buffer.AsSpan(offset + first));
            read = (read + length) % pcm.Length; count -= length;
            Interlocked.Add(ref rendered, length); Interlocked.Add(ref underrun, requested - length);
            if (count != 0) oldest = Math.Min(Native.Clock, oldest + length / 192000.0);
            return requested;
        }
        finally { Monitor.Exit(gate); }
    }
}

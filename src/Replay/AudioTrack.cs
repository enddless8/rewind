using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Replay;
public record AudioPacket(double Start, byte[] Data)
{
    public double End => Start + Data.Length / 192000.0;
    // Assigned before publishing to the queue; snapshots never change historical packets.
    public bool IntentionallyMuted { get; internal set; }
}
public record SignalReport(double Duration, double SilentSeconds, double LongestSilenceSeconds, double PeakDb, double RmsDb)
{
    public double IntentionalMuteSeconds { get; init; }
    public double LongestUnintentionalSilenceSeconds { get; init; }
}

public sealed class AudioTrack : IDisposable
{
    public const int Rate = 48000, Channels = 2, BytesPerFrame = 4;
    private readonly Queue<AudioPacket> packets = new();
    private readonly object gate = new();
    private readonly CancellationTokenSource cancel = new();
    private readonly string? deviceId;
    private readonly int? processId;
    private readonly bool microphone;
    private readonly MicrophoneMuteControl? muteControl;
    private readonly Action<AudioPacket>? previewSink;
    internal string MicrophoneChannelPreference { get; init; } = "Auto";
    internal MicrophoneChannelMode EffectiveMicrophoneChannelMode { get; private set; }
    internal MicrophoneChannelCapability? InputChannelCapability { get; private set; }
    internal string? ChannelModeFeedback { get; private set; }
    private int retention;
    private RecordingAudioWriter? recording;
    private Task? runner;
    private bool disposed;
    public string Name { get; }
    public string? Error { get; private set; }
    public double LastPacket { get; private set; } = Native.Clock;
    public double LastSignal { get; private set; } = Native.Clock;
    public double PeakDb { get; private set; } = -120;
    public double RmsDb { get; private set; } = -120;
    private double gain = 1;
    public double Gain { get => Volatile.Read(ref gain); set => Volatile.Write(ref gain, value); }
    public bool IsMuted => muteControl?.Muted == true;
    public double SignalSilenceSeconds => IsMuted ? 0 : Math.Max(0, Native.Clock - Math.Max(LastSignal, muteControl?.UnmutedAt ?? 0));
    public bool Clipping { get; private set; }
    public double ThresholdDb { get; }
    private AudioTimeline timeline = new();
    public string? DeviceName { get; private set; }
    public string? CaptureFormat { get; private set; }
    public long MissingFrames => timeline.MissingFrames;
    public int Discontinuities => timeline.Discontinuities;
    public AudioTrack(string name, bool microphone, string? deviceId, int? processId, int retention, double threshold, MicrophoneMuteControl? muteControl = null)
        : this(name, microphone, deviceId, processId, retention, threshold, muteControl, null) { }
    internal AudioTrack(string name, bool microphone, string? deviceId, int? processId, int retention, double threshold, MicrophoneMuteControl? muteControl, Action<AudioPacket>? previewSink)
    { Name = name; this.microphone = microphone; this.deviceId = deviceId; this.processId = processId; this.retention = retention; ThresholdDb = threshold; this.muteControl = microphone ? muteControl ?? new() : null; this.previewSink = previewSink; }
    public async Task Start()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner = Task.Factory.StartNew(() =>
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = processId.HasValue ? null : deviceId is null or "default" ? enumerator.GetDefaultAudioEndpoint(microphone ? DataFlow.Capture : DataFlow.Render, microphone ? Role.Communications : Role.Multimedia) : enumerator.GetDevice(deviceId);
                using var client = processId.HasValue ? ProcessActivation.Activate(processId.Value).GetAwaiter().GetResult() : device!.AudioClient;
                var nativeFormat = processId.HasValue ? new WaveFormat(Rate, 16, Channels) : client.MixFormat;
                DeviceName = device?.FriendlyName ?? $"Process {processId}";
                CaptureFormat = nativeFormat.ToString();
                timeline = new AudioTimeline(nativeFormat.SampleRate);
                var converter = CreateConverter(nativeFormat, microphone ? MicrophoneChannels.Inspect(device!, nativeFormat) : null);
                var flags = microphone ? AudioClientStreamFlags.None : AudioClientStreamFlags.Loopback;
                using var audioEvent = new AutoResetEvent(false);
                flags |= AudioClientStreamFlags.EventCallback;
                // Shared-mode capture can buffer 100 ms while still waking on each device event.
                // Avoid a 20 ms buffer serviced by a thread-pool timer (which may wake too late).
                client.Initialize(AudioClientShareMode.Shared, flags, processId.HasValue ? 0 : 1000000, 0, nativeFormat, Guid.Empty);
                client.SetEventHandle(audioEvent.SafeWaitHandle.DangerousGetHandle());
                using var scheduling = new AudioScheduling();
                WaitHandle[] events = [cancel.Token.WaitHandle, audioEvent];
                var capture = client.AudioCaptureClient;
                client.Start(); ready.TrySetResult();
                try
                {
                    while (!cancel.IsCancellationRequested)
                    {
                        while (capture.GetNextPacketSize() > 0)
                        {
                            var ptr = capture.GetBuffer(out var frames, out var status, out var position, out var qpc);
                            byte[] data;
                            try { data = new byte[frames * nativeFormat.BlockAlign]; if ((status & AudioClientBufferFlags.Silent) == 0) Marshal.Copy(ptr, data, 0, data.Length); }
                            finally { capture.ReleaseBuffer(frames); }
                            var timestamp = qpc / 10000000.0;
                            var valid = (status & AudioClientBufferFlags.TimestampError) == 0 && Math.Abs(timestamp - Native.Clock) <= 5;
                            var start = timeline.Place(position, frames, timestamp, valid, (status & AudioClientBufferFlags.DataDiscontinuity) != 0, Native.Clock);
                            var packet = converter.Convert(data, frames, start);
                            ProcessPacket(packet, Native.Clock);
                        }
                        if (WaitHandle.WaitAny(events, 100) == 0) break;
                    }
                }
                finally { client.Stop(); }
            }
            catch (OperationCanceledException) { ready.TrySetCanceled(); }
            catch (Exception ex) { Error = ex.Message; ClipFiles.Log("audio-capture", ex, $"{Name}: {DeviceName}; {CaptureFormat}"); ready.TrySetException(ex); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    public string? Health => Error ?? (microphone && Native.Clock - LastPacket > 3 ? "Микрофон не отдаёт аудиоданные" : null);
    internal NativePcmConverter CreateConverter(WaveFormat format, MicrophoneChannelCapability? capability)
    {
        if (format.Channels <= 1) capability = new(format.Channels, false, "устройство предоставляет один входной канал.");
        InputChannelCapability = microphone ? capability ?? MicrophoneChannelCapability.Unknown : null;
        EffectiveMicrophoneChannelMode = microphone ? (capability ?? MicrophoneChannelCapability.Unknown).Resolve(MicrophoneChannelPreference) : MicrophoneChannelMode.Auto;
        ChannelModeFeedback = microphone ? (capability ?? MicrophoneChannelCapability.Unknown).Feedback(MicrophoneChannelPreference) : null;
        if (EffectiveMicrophoneChannelMode == MicrophoneChannelMode.Stereo && format.Channels > 2)
            ChannelModeFeedback = "Стерео: используются первые два входных канала.";
        return new NativePcmConverter(format, EffectiveMicrophoneChannelMode);
    }
    // Same post-conversion path for live WASAPI and deterministic audio fixtures.
    internal void ProcessPacket(AudioPacket packet, double observedAt)
    {
        LastPacket = observedAt;
        var data = packet.Data;
        if (data.Length == 0) return;
        Clipping = ApplyGain(data, Gain);
        packet.IntentionallyMuted = IsMuted;
        if (packet.IntentionallyMuted) { Array.Clear(data); Clipping = false; PeakDb = RmsDb = -120; }
        else
        {
            var samples = MemoryMarshal.Cast<byte, short>(data.AsSpan());
            double square = 0, peak = 0;
            foreach (var sample in samples) { var value = sample / 32768.0; square += value * value; peak = Math.Max(peak, Math.Abs(value)); }
            PeakDb = Db(peak);
            RmsDb = Db(Math.Sqrt(square / Math.Max(1, samples.Length)));
            if (RmsDb > ThresholdDb) LastSignal = LastPacket;
        }
        lock (gate)
        {
            recording?.Offer(packet);
            if (retention > 0) packets.Enqueue(packet);
            while (packets.Count > 0 && packets.Peek().End < observedAt - retention) packets.Dequeue();
        }
        // Preview consumes processed PCM only; never render or wait on the capture worker.
        previewSink?.Invoke(packet);
    }
    public List<AudioPacket> Snapshot(double start, double end)
    { lock (gate) return packets.Where(p => p.End > start && p.Start < end).ToList(); }
    internal void SetRetention(int seconds)
    { lock (gate) { retention = seconds; packets.Clear(); } }
    internal void AttachRecording(RecordingAudioWriter writer, double start)
    {
        lock (gate)
        {
            if (recording != null) throw new InvalidOperationException("Аудиодорожка уже используется записью.");
            foreach (var packet in packets.Where(p => p.End > start)) writer.Offer(packet);
            recording = writer;
        }
    }
    internal void DetachRecording() { lock (gate) recording = null; }
    public static double Db(double amplitude) => amplitude > 0 ? 20 * Math.Log10(amplitude) : -120;
    internal static bool ApplyGain(byte[] data, double gain)
    {
        bool clipped = false;
        var samples = MemoryMarshal.Cast<byte, short>(data.AsSpan());
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i] * gain;
            clipped |= value > short.MaxValue || value < short.MinValue;
            samples[i] = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
        }
        return clipped;
    }
    public static SignalReport WriteWave(string path, List<AudioPacket> source, double start, double duration, double thresholdDb)
    {
        var frames = (long)Math.Round(duration * Rate);
        using (var writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, Channels)))
        {
            var silence = new byte[Rate * BytesPerFrame];
            for (long left = frames * BytesPerFrame; left > 0; left -= Math.Min(left, silence.Length)) writer.Write(silence, 0, (int)Math.Min(left, silence.Length));
        }
        // WAV header size is read, not assumed: NAudio may emit an extra fmt chunk.
        long dataOffset;
        using (var reader = new WaveFileReader(path)) dataOffset = new FileInfo(path).Length - reader.Length;
        {
            using var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            foreach (var packet in source)
            {
                var at = (long)Math.Round((packet.Start - start) * Rate);
                var skip = Math.Max(0, -at);
                var count = Math.Min(packet.Data.Length / BytesPerFrame - skip, frames - Math.Max(0, at));
                if (count <= 0) continue;
                output.Position = dataOffset + Math.Max(0, at) * BytesPerFrame;
                output.Write(packet.Data, checked((int)(skip * BytesPerFrame)), checked((int)(count * BytesPerFrame)));
            }
        }
        return Analyze(path, thresholdDb, MutedFrames(source, start, frames));
    }
    private static List<(long Start, long End)> MutedFrames(List<AudioPacket> source, double start, long frames)
    {
        var spans = source.Where(p => p.IntentionallyMuted)
            .Select(p => (Start: Math.Max(0, (long)Math.Round((p.Start - start) * Rate)), End: Math.Min(frames, (long)Math.Round((p.End - start) * Rate))))
            .Where(p => p.End > p.Start).OrderBy(p => p.Start);
        List<(long Start, long End)> merged = [];
        foreach (var span in spans)
            if (merged.Count > 0 && span.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(span.End, merged[^1].End));
            else merged.Add(span);
        return merged;
    }
    public static SignalReport Analyze(string path, double thresholdDb) => Analyze(path, thresholdDb, []);
    private static SignalReport Analyze(string path, double thresholdDb, List<(long Start, long End)> mutedFrames)
    {
        using var reader = new WaveFileReader(path);
        var data = new byte[Rate / 50 * BytesPerFrame]; // 20 ms RMS windows
        double square = 0, peak = 0, silent = 0, longest = 0, run = 0, unexpectedRun = 0, unexpectedLongest = 0;
        long samples = 0, frameAt = 0; int muteIndex = 0;
        int count;
        while ((count = reader.Read(data, 0, data.Length)) > 0)
        {
            var pcm = MemoryMarshal.Cast<byte, short>(data.AsSpan(0, count));
            double local = 0;
            foreach (var s in pcm) { var v = s / 32768.0; local += v * v; peak = Math.Max(peak, Math.Abs(v)); }
            square += local; samples += pcm.Length;
            var seconds = count / 192000.0;
            if (Db(Math.Sqrt(local / Math.Max(1, pcm.Length))) <= thresholdDb) { silent += seconds; run += seconds; longest = Math.Max(longest, run); } else run = 0;
            // Split RMS windows at mute edges, so intentional silence never hides a real gap.
            var frameEnd = frameAt + count / BytesPerFrame;
            for (var at = frameAt; at < frameEnd;)
            {
                while (muteIndex < mutedFrames.Count && mutedFrames[muteIndex].End <= at) muteIndex++;
                var intentional = muteIndex < mutedFrames.Count && mutedFrames[muteIndex].Start <= at;
                var until = muteIndex == mutedFrames.Count ? frameEnd : Math.Min(frameEnd,
                    intentional ? mutedFrames[muteIndex].End : mutedFrames[muteIndex].Start);
                if (intentional) unexpectedRun = 0;
                else
                {
                    var part = pcm.Slice(checked((int)((at - frameAt) * Channels)), checked((int)((until - at) * Channels)));
                    double energy = 0;
                    foreach (var s in part) { var v = s / 32768.0; energy += v * v; }
                    if (Db(Math.Sqrt(energy / Math.Max(1, part.Length))) <= thresholdDb)
                    { unexpectedRun += (until - at) / (double)Rate; unexpectedLongest = Math.Max(unexpectedLongest, unexpectedRun); }
                    else unexpectedRun = 0;
                }
                at = until;
            }
            frameAt = frameEnd;
        }
        return new(samples / (double)(Rate * Channels), silent, longest, Db(peak), Db(Math.Sqrt(square / Math.Max(1, samples))))
        { IntentionalMuteSeconds = mutedFrames.Sum(s => s.End - s.Start) / (double)Rate, LongestUnintentionalSilenceSeconds = unexpectedLongest };
    }
    internal async Task StopPreviewAsync()
    {
        if (disposed) return;
        disposed = true;
        cancel.Cancel();
        if (runner != null) { try { await runner.ConfigureAwait(false); } catch { } }
        cancel.Dispose();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancel.Cancel();
        try { runner?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        cancel.Dispose();
    }
}

public static class ProcessActivation
{
    [StructLayout(LayoutKind.Sequential)] private struct Parameters { public int Type; public uint Pid; public int Mode; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant
    { [FieldOffset(0)] public short Type; [FieldOffset(8)] public int Size; [FieldOffset(16)] public nint Data; }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOperation { [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated); }
    [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICompletion { [PreserveSig] int ActivateCompleted(IOperation operation); }
    [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgile { }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Completion : ICompletion, IAgile
    {
        public readonly TaskCompletionSource<AudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActivateCompleted(IOperation operation)
        {
            try { Marshal.ThrowExceptionForHR(operation.GetActivateResult(out var hr, out var obj)); Marshal.ThrowExceptionForHR(hr); Result.TrySetResult(new AudioClient((IAudioClient)obj)); }
            catch (Exception ex) { Result.TrySetException(ex); }
            return 0;
        }
    }
    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string device, [In] ref Guid iid, ref Variant parameters, ICompletion completion, out IOperation operation);
    public static async Task<AudioClient> Activate(int pid)
    {
        var memory = Marshal.AllocCoTaskMem(12);
        try
        {
            Marshal.StructureToPtr(new Parameters { Type = 1, Pid = (uint)pid, Mode = 0 }, memory, false);
            var variant = new Variant { Type = 65, Size = 12, Data = memory };
            var iid = typeof(IAudioClient).GUID;
            var completion = new Completion();
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, ref variant, completion, out var operation));
            // Activation owns a copy of the parameters. Keep the COM operation alive until completion.
            try { return await completion.Result.Task; }
            finally { GC.KeepAlive(completion); Marshal.ReleaseComObject(operation); }
        }
        finally { Marshal.FreeCoTaskMem(memory); }
    }
}

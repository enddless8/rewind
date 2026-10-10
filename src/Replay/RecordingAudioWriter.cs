using System.Threading.Channels;

namespace Replay;

// Receives the SAME post-gain/post-mute packets as Rewind. The WASAPI worker never waits on disk.
internal sealed class RecordingAudioWriter
{
    internal const int QueueByteLimit = 4 * 1024 * 1024;
    private readonly Channel<AudioPacket> queue = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(512)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task worker;
    private readonly double start;
    private long queuedBytes;
    private double end = double.PositiveInfinity;
    private string? error;
    public string Name { get; }
    public string Path { get; }
    public string? Error => Volatile.Read(ref error);
    public long QueuedBytes => Interlocked.Read(ref queuedBytes);
    public RecordingAudioWriter(string directory, string name, double start)
    {
        Name = name; Path = System.IO.Path.Combine(directory, name + ".pcm"); this.start = start;
        // Open before attaching: permission failures cannot escape from an audio callback.
        var output = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
        worker = Task.Run(() => Write(output, System.IO.Path.Combine(directory, name + ".mute.jsonl")));
    }
    public void Offer(AudioPacket packet)
    {
        if (packet.End <= start || packet.Start >= Volatile.Read(ref end) || Error != null) return;
        var bytes = Interlocked.Add(ref queuedBytes, packet.Data.Length);
        if (bytes > QueueByteLimit || !queue.Writer.TryWrite(packet))
        {
            Interlocked.Add(ref queuedBytes, -packet.Data.Length);
            Interlocked.CompareExchange(ref error, "Очередь аудио переполнена: запись остановлена, данные сохранены.", null);
        }
    }
    private async Task Write(FileStream output, string mutePath)
    {
        using (output)
        using (var mute = new StreamWriter(mutePath, false))
        {
            try
            {
                await foreach (var packet in queue.Reader.ReadAllAsync())
                {
                    Interlocked.Add(ref queuedBytes, -packet.Data.Length);
                    var at = (long)Math.Round((packet.Start - start) * AudioTrack.Rate);
                    var skip = Math.Max(0, -at);
                    var frames = Math.Min(packet.Data.Length / 4 - skip,
                        double.IsFinite(Volatile.Read(ref end)) ? (long)Math.Round((end - start) * AudioTrack.Rate) - Math.Max(0, at) : long.MaxValue);
                    if (frames <= 0) continue;
                    output.Position = Math.Max(0, at) * 4;
                    output.Write(packet.Data, checked((int)(skip * 4)), checked((int)(frames * 4)));
                    if (packet.IntentionallyMuted)
                        mute.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Start = Math.Max(0, at) / 48000.0, Duration = frames / 48000.0 }));
                    // Flush bounded chunks; a process crash loses at most the current disk buffer.
                    output.Flush(); mute.Flush();
                }
                if (double.IsFinite(end)) output.SetLength(Math.Max(0, (long)Math.Round((end - start) * 48000)) * 4);
                output.Flush(true);
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref error, "Не удалось записать аудио: " + ex.Message, null); }
            finally { while (queue.Reader.TryRead(out var packet)) Interlocked.Add(ref queuedBytes, -packet.Data.Length); }
        }
    }
    public async Task Complete(double stop)
    {
        Volatile.Write(ref end, Math.Max(start, stop)); queue.Writer.TryComplete(); await worker;
    }
}

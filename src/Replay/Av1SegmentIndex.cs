using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Replay;

// Deliberately restricted to ordinary unencrypted, unlaced FFmpeg Matroska AV1.
// No muxer or AV1 decoder is implemented here. Unknown structures fail closed.
internal sealed record Av1SegmentIndex(long TimeScale, long DefaultDuration, string CodecPrivate,
    string VideoProperties, IReadOnlyList<Av1SegmentIndex.Packet> Packets)
{
    // Matroska carries PTS; DTS is equal only in the positively verified
    // one-shown-frame/no-reordering subset. Duration is DefaultDuration in ns.
    internal sealed record Packet(long Pts, string Hash, bool RandomAccess, long Duration = 0) { internal long Dts => Pts; }
    internal string Codec => "av1";
    internal string Profile => "Main";
    internal int BitDepth => (Convert.FromHexString(CodecPrivate)[2] & 0x40) == 0 ? 8 : 10;
    private readonly record struct Identity(uint Volume, ulong File, long Size, long Written);
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    private static readonly object gate = new();
    private static readonly Dictionary<Identity, Av1SegmentIndex> cache = new();
    internal static Av1SegmentIndex Read(string path, bool cached = true)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var info)) throw new IOException("Не удалось проверить идентичность видеосегмента.");
        var id = new Identity(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow, file.Length,
            ((long)info.Written.dwHighDateTime << 32) | (uint)info.Written.dwLowDateTime);
        // Some filesystems do not expose a reliable identity. Still inspect the
        // owned file, but never share an index using an ambiguous cache key.
        cached &= id.Volume != 0 && id.File != 0;
        lock (gate) if (cached && cache.TryGetValue(id, out var found)) return found;
        var result = new Reader(file).Read();
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var after) || file.Length != id.Size ||
            after.Written.dwLowDateTime != info.Written.dwLowDateTime || after.Written.dwHighDateTime != info.Written.dwHighDateTime)
            throw new IOException("Видеосегмент изменился во время проверки.");
        lock (gate)
        {
            if (cached) { if (cache.Count >= 512) cache.Clear(); cache[id] = result; }
        }
        return result;
    }
    internal bool Compatible(Av1SegmentIndex other) => TimeScale == other.TimeScale &&
        DefaultDuration == other.DefaultDuration && CodecPrivate == other.CodecPrivate && VideoProperties == other.VideoProperties;

    // AV1 spec 5.3/5.9: shown KEY_FRAME resets all references. Accept only one
    // OBU_FRAME per packet, no hidden/show-existing/intra-only/switch/layered frames.
    // With every frame shown and strictly ordered PTS there are no future display
    // dependencies; a prefix ending inside this low-delay GOP can be decoded.
    internal static bool InspectPacket(ReadOnlySpan<byte> data, byte[] sequence, out bool key)
    {
        key = false; var frames = 0; var hasSequence = false;
        while (data.Length > 0)
        {
            var header = data[0]; data = data[1..];
            if ((header & 0x81) != 0 || (header & 2) == 0) return false;
            if ((header & 4) != 0) { if (data.Length == 0 || data[0] != 0) return false; data = data[1..]; }
            var type = (header >> 3) & 15; ulong size = 0; var shift = 0; byte part;
            do
            {
                if (data.Length == 0 || shift >= 56) return false;
                part = data[0]; data = data[1..]; size |= (ulong)(part & 127) << shift; shift += 7;
            } while ((part & 128) != 0);
            if (size > (ulong)data.Length) return false;
            var payload = data[..(int)size]; data = data[(int)size..];
            switch (type)
            {
                case 1:
                    if (frames != 0 || hasSequence || !payload.SequenceEqual(sequence)) return false;
                    hasSequence = true; break;
                case 2: if (payload.Length != 0 || frames != 0) return false; break;
                case 6:
                    if (++frames != 1 || payload.Length == 0 || (payload[0] & 0x80) != 0) return false;
                    var frameType = (payload[0] >> 5) & 3;
                    if (frameType > 1 || (payload[0] & 0x10) == 0) return false;
                    key = frameType == 0; break;
                case 15: break; // AV1 padding
                default: return false;
            }
        }
        return frames == 1 && (!key || hasSequence);
    }
    private sealed class Reader(FileStream file)
    {
        private long timeScale = 1_000_000, duration, videoTrack, cluster;
        private string codec = ""; private byte[] privateData = []; private byte[] sequence = [];
        private readonly SortedDictionary<string, string> video = new();
        private readonly List<Packet> packets = [];
        private (ulong Value, int Length) Vint(bool identifier = false)
        {
            var first = file.ReadByte(); if (first <= 0) throw new IOException("Некорректный EBML.");
            var length = 1; while ((first & (1 << (8 - length))) == 0 && length < 8) length++;
            ulong value = (ulong)(identifier ? first : first & ((1 << (8 - length)) - 1));
            for (var i = 1; i < length; i++) { var next = file.ReadByte(); if (next < 0) throw new EndOfStreamException(); value = value * 256 + (uint)next; }
            return (value, length);
        }
        private byte[] Bytes(long end)
        {
            var length = end - file.Position;
            if (length is < 0 or > 16 * 1024 * 1024) throw new IOException("Неподдерживаемый размер EBML-элемента.");
            var bytes = new byte[(int)length]; file.ReadExactly(bytes); return bytes;
        }
        private ulong Unsigned(long end)
        {
            var bytes = Bytes(end); if (bytes.Length > 8) throw new IOException("Некорректное число EBML.");
            ulong value = 0; foreach (var b in bytes) value = value * 256 + b; return value;
        }
        internal Av1SegmentIndex Read()
        {
            Elements(file.Length, 0);
            if (codec != "V_AV1" || videoTrack == 0 || packets.Count == 0 || timeScale != 1_000_000 || duration <= 0)
                throw new IOException("Неподдерживаемый формат packet-copy.");
            if (!video.TryGetValue("Video/B0", out var width) || !video.TryGetValue("Video/BA", out var height) ||
                Convert.FromHexString(width).All(b => b == 0) || Convert.FromHexString(height).All(b => b == 0))
                throw new IOException("Не определено разрешение AV1.");
            return new(timeScale, duration, Convert.ToHexString(privateData), string.Join(";", video.Select(p => p.Key + "=" + p.Value)), packets.AsReadOnly());
        }
        private void Elements(long end, int depth, string? videoPath = null)
        {
            if (depth > 8) throw new IOException("Некорректная вложенность EBML.");
            while (file.Position < end)
            {
                var id = Vint(true).Value; var (size, length) = Vint();
                var unknown = size == ((1UL << (7 * length)) - 1);
                if (unknown && id != 0x18538067) throw new IOException("Неизвестная длина EBML.");
                if (!unknown && size > (ulong)(end - file.Position)) throw new IOException("Обрезанный EBML.");
                var stop = unknown ? end : file.Position + (long)size;
                if (videoPath != null)
                {
                    if (id == 0x55B0) Elements(stop, depth + 1, videoPath + "/55B0");
                    else video[videoPath + "/" + id.ToString("X")] = Convert.ToHexString(Bytes(stop));
                }
                else switch (id)
                {
                    case 0x18538067: case 0x1549A966: case 0x1654AE6B: Elements(stop, depth + 1); break;
                    case 0x2AD7B1: timeScale = checked((long)Unsigned(stop)); break;
                    case 0xAE: Track(stop); break;
                    case 0x1F43B675: cluster = -1; Elements(stop, depth + 1); break;
                    case 0xE7: cluster = checked((long)Unsigned(stop)); break;
                    case 0xA3: Block(stop); break;
                    case 0xA0:
                        // AAC may use a BlockGroup for discard padding. Video
                        // BlockGroups/reference blocks are outside this subset.
                        var blockId = Vint(true).Value; var blockSize = Vint().Value;
                        if (blockId != 0xA1 || blockSize > (ulong)(stop - file.Position) || (long)Vint().Value == videoTrack)
                            throw new IOException("Video BlockGroup требует отдельной проверки зависимостей.");
                        break;
                }
                file.Position = stop;
            }
        }
        private void Track(long end)
        {
            long number = 0, type = 0, defaultDuration = 0; var trackCodec = ""; byte[] data = []; long videoStart = 0, videoEnd = 0;
            var transformedTimeline = false;
            while (file.Position < end)
            {
                var id = Vint(true).Value; var size = Vint().Value;
                if (size > (ulong)(end - file.Position)) throw new IOException("Некорректная дорожка.");
                var stop = file.Position + (long)size;
                switch (id)
                {
                    case 0xD7: number = checked((long)Unsigned(stop)); break;
                    case 0x83: type = checked((long)Unsigned(stop)); break;
                    case 0x86: trackCodec = Encoding.UTF8.GetString(Bytes(stop)); break;
                    case 0x63A2: data = Bytes(stop); break;
                    case 0x23E383: defaultDuration = checked((long)Unsigned(stop)); break;
                    case 0xE0: videoStart = file.Position; videoEnd = stop; break;
                    case 0x23314F: case 0x537F: case 0x56AA: case 0x56BB: case 0xE2:
                        transformedTimeline = true; break; // timestamp scale/offset, codec delay/preroll, TrackOperation
                    case 0x6D80: throw new IOException("Закодированные/encrypted дорожки не копируются.");
                }
                file.Position = stop;
            }
            if (type != 1) return;
            if (transformedTimeline || videoTrack != 0 || trackCodec != "V_AV1" || data.Length < 7 || data[0] != 0x81 ||
                (data[1] >> 5) != 0 || (data[2] & 0x3f) != 0x0c || (data[3] & 0xf0) != 0)
                throw new IOException("Packet-copy поддерживает только обычный AV1 Main 4:2:0 8/10-bit.");
            // av1C contains the exact sequence-header OBU. Reduced still-picture
            // syntax changes frame-header parsing and is intentionally rejected.
            var obu = data.AsSpan(4); if (obu[0] != 0x0a || obu[1] >= 128 || obu.Length != obu[1] + 2 || (obu[2] & 0xf8) != 0)
                throw new IOException("Неизвестный AV1 sequence header.");
            sequence = obu[2..].ToArray(); videoTrack = number; codec = trackCodec; privateData = data; duration = defaultDuration;
            if (videoStart == 0) throw new IOException("Нет параметров видео.");
            file.Position = videoStart; Elements(videoEnd, 1, "Video"); file.Position = end;
        }
        private void Block(long end)
        {
            var track = Vint().Value;
            if ((long)track != videoTrack) return;
            if (videoTrack == 0 || cluster < 0 || end - file.Position < 4) throw new IOException("Неопределённая видеодорожка.");
            Span<byte> header = stackalloc byte[3]; file.ReadExactly(header);
            if ((header[2] & 0x7f) != 0) throw new IOException("Laced/invisible/discardable/reserved block flags не копируются.");
            var pts = checked(cluster + BinaryPrimitives.ReadInt16BigEndian(header));
            var data = Bytes(end);
            if (pts < 0 || (packets.Count > 0 && pts <= packets[^1].Pts) || !InspectPacket(data, sequence, out var key) ||
                key != ((header[2] & 0x80) != 0) || (packets.Count == 0 && (!key || pts != 0)))
                throw new IOException("Не доказана независимость/порядок кадров AV1.");
            if (packets.Count >= 360_000) throw new IOException("Слишком много пакетов для ограниченного индекса.");
            packets.Add(new(pts, Convert.ToHexString(SHA256.HashData(data)), key, duration));
        }
    }
}

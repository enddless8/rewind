using System.Text.Json;

namespace Replay;
public sealed record CompressionProfile
{
    public string Codec { get; init; } = "h265";
    public string Preset { get; init; } = "Сильнее сжать";
    public int Height { get; init; } = 1080;
    public int Fps { get; init; } = 30;
    public int Cq => Preset switch { "Макс качество" => 21, "Баланс" => 27, "Сильнее сжать" => 31, "Минимальный размер" => 35, _ => throw new ArgumentException("Неизвестный пресет компрессора") };
    public string Encoder => Codec == "h264" ? "h264_nvenc" : "hevc_nvenc";
    public string Summary => $"{(Codec == "h265" ? "HEVC" : "H.264")} · {Preset} · {(Height == 0 ? "исходное разрешение" : Height + "p")} · {(Fps == 0 ? "исходные FPS" : Fps + " FPS")} · все аудиодорожки";
    public void Validate()
    {
        if (Codec is not ("h264" or "h265") || Height is not (0 or 480 or 720 or 1080 or 1440 or 2160) || Fps is not (0 or 24 or 30 or 60)) throw new ArgumentException("Некорректный профиль сжатия");
        _ = Cq;
    }
    public static CompressionProfile Import(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var settings = json.RootElement.TryGetProperty("compress", out var nested) ? nested : json.RootElement;
        string Value(string key, string fallback) => settings.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;
        var profile = new CompressionProfile { Codec = Value("codec", "h265"), Preset = Value("preset", "Сильнее сжать"),
            Height = int.TryParse(Value("resolution", "1080p").TrimEnd('p'), out var h) ? h : 0,
            Fps = int.TryParse(Value("fps", "30"), out var f) ? f : 0 };
        profile.Validate(); return profile;
    }
    // Port of MP4 COMPRESSOR/mp4compressor/ffmpeg_tools.py build_compress_command.
    // Preserve every audio stream, never inherit delete_source or overwrite options.
    public List<string> Arguments(string source, string target)
    {
        Validate();
        List<string> args = ["-hide_banner", "-v", "error", "-nostats", "-stats_period", "0.5", "-progress", "pipe:1", "-y", "-threads", "2", "-filter_threads", "2", "-i", source];
        List<string> filters = [];
        if (Height > 0) filters.Add($"scale=-2:'min({Height},ih)':force_original_aspect_ratio=decrease");
        if (Fps > 0) filters.Add($"fps={Fps}");
        if (filters.Count > 0) args.AddRange(["-vf", string.Join(',', filters)]);
        args.AddRange(["-map", "0:v:0", "-map", "0:a?", "-c:v", Encoder, "-preset", "p5", "-cq", Cq.ToString(), "-b:v", "0", "-pix_fmt", "yuv420p", "-c:a", "copy", "-map_metadata", "0", "-movflags", "+faststart"]);
        if (Codec == "h265") args.AddRange(["-tag:v", "hvc1"]);
        args.Add(target); return args;
    }
}

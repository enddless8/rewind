using System.Text.Json;

namespace Replay;
public sealed class Settings
{
    // Keep the legacy directory: existing settings, queue and buffer paths must survive rebranding.
    internal static string? DataDirectoryForTests { get; set; }
    public static string DataDirectory => DataDirectoryForTests ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Replay");
    public string CaptureMode { get; set; } = "Game";
    public string AudioMode { get; set; } = "Game";
    public string Codec { get; set; } = "Auto";
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public int BufferSeconds { get; set; } = 180;
    public int Quality { get; set; } = 27;
    public int MaxBitrateMbps { get; set; } = 16;
    public int MaxBufferMegabytes { get; set; } = 2048;
    public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Rewind");
    public bool GroupByApplication { get; set; } = true;
    public string MicrophoneId { get; set; } = "default";
    public string MicrophoneChannelMode { get; set; } = "Auto";
    public double MicrophoneVolume { get; set; } = 100;
    public double SystemVolume { get; set; } = 100;
    public double MicrophoneBoostDb { get; set; }
    public double MicrophoneGain => MicrophoneVolume / 100 * Math.Pow(10, MicrophoneBoostDb / 20);
    public string BufferDirectory { get; set; } = Path.Combine(DataDirectory, "buffer");
    public bool Discord { get; set; } = true;
    public bool Sounds { get; set; } = true;
    public bool ShowServiceInfo { get; set; }
    public bool HideDisplayBorder { get; set; } = true;
    public bool CompressAfterSave { get; set; }
    public CompressionProfile Compression { get; set; } = new();
    public double SilenceDb { get; set; } = -55;
    public int SilenceWarningSeconds { get; set; } = 30;
    public int AudioOffsetMs { get; set; }
    public uint HotkeyModifiers { get; set; } = 3; // Ctrl + Alt
    public uint HotkeyKey { get; set; } = 0x79; // F10
    public bool HotkeyEnabled { get; set; } = true;
    public uint ToggleHotkeyModifiers { get; set; } = 3; // Ctrl + Alt
    public uint ToggleHotkeyKey { get; set; } = 0x77; // F8 for fresh installations
    public bool ToggleHotkeyEnabled { get; set; } = true;
    public uint RecordingHotkeyModifiers { get; set; } = 3;
    public uint RecordingHotkeyKey { get; set; } = 0x78; // F9
    public bool RecordingHotkeyEnabled { get; set; } = true;
    public uint ScreenshotHotkeyModifiers { get; set; } = 3;
    public uint ScreenshotHotkeyKey { get; set; } = 0x70; // F1
    public bool ScreenshotHotkeyEnabled { get; set; } = true;
    public uint MicrophoneHotkeyModifiers { get; set; } = 3;
    public uint MicrophoneHotkeyKey { get; set; }
    public bool MicrophoneHotkeyEnabled { get; set; }
    public string GameExe { get; set; } = "";
    public string MonitorDevice { get; set; } = "";
    public Dictionary<string,string> GameNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public void Validate()
    {
        Compression.Validate();
        if (MicrophoneChannelMode is not ("Auto" or "Channel1" or "Channel2" or "MonoMix" or "Stereo")) throw new ArgumentException("Неизвестный режим микрофона.");
        if (!double.IsFinite(MicrophoneVolume) || !double.IsFinite(SystemVolume) || !double.IsFinite(MicrophoneBoostDb) || MicrophoneVolume is < 0 or > 100 || SystemVolume is < 0 or > 100 || MicrophoneBoostDb is < 0 or > 30) throw new ArgumentException("Громкость: 0–100%; усиление микрофона: 0–30 dB.");
        if (CaptureMode is not ("Game" or "Desktop") || AudioMode is not ("Game" or "System")) throw new ArgumentException("Неизвестный режим захвата.");
        if (Codec is not ("Auto" or "HEVC" or "AV1" or "H264")) throw new ArgumentException("Неизвестный кодек.");
        if (Fps is not (30 or 60) || Height is not (720 or 1080 or 1440 or 2160)) throw new ArgumentException("Некорректные FPS или разрешение.");
        if (BufferSeconds < 15 || BufferSeconds > 600) throw new ArgumentException("Буфер: от 15 до 600 секунд.");
        if (MaxBitrateMbps < 2 || MaxBitrateMbps > 100 || Quality < 15 || Quality > 40) throw new ArgumentException("Некорректное качество или битрейт.");
        if (MaxBufferMegabytes < 128 || MaxBufferMegabytes > 16384) throw new ArgumentException("Лимит буфера: 128–16384 МБ.");
        if (SilenceDb < -90 || SilenceDb > -10 || AudioOffsetMs < -2000 || AudioOffsetMs > 2000) throw new ArgumentException("Некорректный порог или сдвиг аудио.");
        if (!Path.IsPathFullyQualified(OutputDirectory)) throw new ArgumentException("Выбери абсолютный путь для клипов.");
        if (string.IsNullOrWhiteSpace(BufferDirectory) || !Path.IsPathFullyQualified(BufferDirectory)) throw new ArgumentException("Выбери абсолютный путь для временных файлов буфера.");
    }
    public static Settings Load()
    {
        var file = Path.Combine(DataDirectory, "settings.json");
        if (!File.Exists(file)) return new();
        try { var value = FromJson(File.ReadAllText(file)); value.Validate(); return value; }
        catch { File.Copy(file, file + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); return new(); }
    }
    // Loading is read-only. Missing fields distinguish pre-four-action configurations;
    // serialization clones already contain every field and need no migration.
    public static Settings FromJson(string json)
    {
        var value = JsonSerializer.Deserialize<Settings>(json) ?? new();
        // Read-only compatibility with the previous minimum. Upper-bound validation stays unchanged.
        value.BufferSeconds = Math.Max(15, value.BufferSeconds);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return value;
        bool Has(string name) => document.RootElement.TryGetProperty(name, out _);
        if (!Has(nameof(RecordingHotkeyKey)) && !Has(nameof(ScreenshotHotkeyKey)) && !Has(nameof(ToggleHotkeyKey)))
            value.ToggleHotkeyKey = 0x78; // The legacy implicit default was F9.
        bool Conflicts(uint modifiers, uint key) => key != 0 &&
            (modifiers == value.HotkeyModifiers && key == value.HotkeyKey ||
             modifiers == value.ToggleHotkeyModifiers && key == value.ToggleHotkeyKey);
        if (!Has(nameof(RecordingHotkeyKey)) && Conflicts(value.RecordingHotkeyModifiers, value.RecordingHotkeyKey))
        { value.RecordingHotkeyKey = 0; value.RecordingHotkeyEnabled = false; }
        if (!Has(nameof(ScreenshotHotkeyKey)) && (Conflicts(value.ScreenshotHotkeyModifiers, value.ScreenshotHotkeyKey) ||
            value.RecordingHotkeyKey != 0 && value.ScreenshotHotkeyModifiers == value.RecordingHotkeyModifiers && value.ScreenshotHotkeyKey == value.RecordingHotkeyKey))
        { value.ScreenshotHotkeyKey = 0; value.ScreenshotHotkeyEnabled = false; }
        return value;
    }
    public void Save()
    {
        Validate(); Directory.CreateDirectory(DataDirectory);
        var file = Path.Combine(DataDirectory, "settings.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }
}

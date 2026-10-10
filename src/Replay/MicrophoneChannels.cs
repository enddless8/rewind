using System.Numerics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Replay;

internal enum MicrophoneChannelMode { Auto, Channel1, Channel2, MonoMix, Stereo }

internal sealed record MicrophoneChannelCapability(int Channels, bool IndependentInputs, string Reason)
{
    internal bool ShowMode => Channels > 1 && IndependentInputs;
    internal MicrophoneChannelMode Resolve(string preference) => ShowMode ? Enum.Parse<MicrophoneChannelMode>(preference) : MicrophoneChannelMode.Auto;
    internal string? Feedback(string preference) => preference == "Auto" || ShowMode ? null
        : "Используется «Авто»: " + Reason + " Сохранённый режим будет доступен на совместимом устройстве.";
    internal static MicrophoneChannelCapability Unknown { get; } = new(0, false, "независимые входы устройства не подтверждены.");
}

internal static class MicrophoneChannels
{
    private static readonly Guid LineInput = new("DFF21FE3-F70F-11D0-B917-00A0C9223196");
    private static readonly Guid Mux = new("2CEAF780-C556-11D0-8A2B-00A0C9255AC1");
    private static readonly Guid Sum = new("DA441A60-C556-11D0-8A2B-00A0C9255AC1");
    private static readonly Guid MicrophoneArray = new("DFF21BE5-F70F-11D0-B917-00A0C9223196");

    internal static MicrophoneChannelCapability Inspect(string id)
    {
        if (id == "off") return MicrophoneChannelCapability.Unknown;
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = id == "default" ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : devices.GetDevice(id);
            using var client = device.AudioClient;
            return Inspect(device, client.MixFormat);
        }
        catch { return MicrophoneChannelCapability.Unknown; }
    }
    internal static MicrophoneChannelCapability Inspect(MMDevice device, WaveFormat format)
    {
        if (device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active) return new(format.Channels, false, "микрофон недоступен.");
        if (format.Channels <= 1) return new(format.Channels, false, "устройство предоставляет один входной канал.");
        try { return FromTopology(format.Channels, ChannelMask(format), AudioInputTopology.Read(device.ID)); }
        catch { return new(format.Channels, false, MicrophoneChannelCapability.Unknown.Reason); }
    }
    private static uint ChannelMask(WaveFormat format)
    {
        if (format is not WaveFormatExtensible) return 0;
        // WAVEFORMATEXTENSIBLE.dwChannelMask follows WAVEFORMATEX (18 bytes)
        // and wValidBitsPerSample (2 bytes). NAudio does not expose it publicly.
        var pointer = WaveFormat.MarshalToPtr(format);
        try { return unchecked((uint)Marshal.ReadInt32(pointer, 20)); }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    // MixFormat alone cannot distinguish independent line inputs from a stereo USB mic.
    // Require an unmixed hardware line terminal, or disjoint physical-jack channel maps.
    // Missing/ambiguous topology and microphone arrays deliberately keep legacy Auto.
    internal static MicrophoneChannelCapability FromTopology(int channels, uint channelMask, AudioInputTopology.Route route)
    {
        if (channels <= 1) return new(channels, false, "устройство предоставляет один входной канал.");
        if (route.Nodes.Any(n => n.Subtype == Mux || n.Subtype == Sum || n.Subtype == MicrophoneArray))
            return new(channels, false, "маршрут независимых входов устройства не подтверждён.");
        var line = route.Nodes.Any(n => n.Type == 0 && n.Subtype == LineInput);
        uint mapped = 0; var disjoint = true;
        var jacks = route.Jacks.Where(j => j.Port == 0 && j.Channels != 0).ToArray();
        foreach (var jack in jacks) { disjoint &= (mapped & jack.Channels) == 0; mapped |= jack.Channels; }
        var separateJacks = jacks.Length > 1 && disjoint && channelMask != 0 && mapped == channelMask && BitOperations.PopCount(mapped) == channels;
        return new(channels, line || separateJacks, line || separateJacks ? "Подтверждён многоканальный вход." : MicrophoneChannelCapability.Unknown.Reason);
    }
}

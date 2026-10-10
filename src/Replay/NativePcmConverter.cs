using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;

namespace Replay;

// Capture in the endpoint's existing mix format. Resample inside Replay, without
// asking a USB driver to convert its shared render/capture streams to 48 kHz.
internal sealed class NativePcmConverter
{
    private readonly WaveFormat format;
    private readonly bool floating;
    private readonly MicrophoneChannelMode channelMode;
    private readonly WdlResampler? resampler;
    private double nextInput = double.NaN, nextOutput;
    private long inputFrames, emittedFrames;
    public NativePcmConverter(WaveFormat format, MicrophoneChannelMode channelMode = MicrophoneChannelMode.Auto)
    {
        this.format = format;
        this.channelMode = format.Channels > 1 ? channelMode : MicrophoneChannelMode.Auto;
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extended)
        {
            if (extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.IeeeFloat;
            else if (extended.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")) encoding = WaveFormatEncoding.Pcm;
        }
        floating = encoding == WaveFormatEncoding.IeeeFloat;
        if (format.SampleRate <= 0 || format.Channels < 1 || format.BlockAlign != format.Channels * format.BitsPerSample / 8 ||
            (floating ? format.BitsPerSample is not (32 or 64) : encoding != WaveFormatEncoding.Pcm || format.BitsPerSample is not (8 or 16 or 24 or 32)))
            throw new NotSupportedException($"Неподдерживаемый формат аудиоустройства: {format}");
        if (format.SampleRate != AudioTrack.Rate)
        {
            resampler = new WdlResampler();
            resampler.SetMode(true, 2, false);
            resampler.SetFilterParms();
            resampler.SetFeedMode(true);
            resampler.SetRates(format.SampleRate, AudioTrack.Rate);
        }
    }
    public AudioPacket Convert(byte[] source, int frames, double start)
    {
        if (source.Length != frames * format.BlockAlign) throw new ArgumentException("Incomplete native audio packet.");
        if (double.IsNaN(nextInput) || Math.Abs(start - nextInput) > 1.5 / format.SampleRate)
        {
            resampler?.Reset(); nextOutput = start; inputFrames = emittedFrames = 0;
        }
        nextInput = start + frames / (double)format.SampleRate;
        inputFrames += frames;
        float[] stereo;
        int at;
        if (resampler != null) resampler.ResamplePrepare(frames, 2, out stereo, out at);
        else { stereo = new float[frames * 2]; at = 0; }
        var bytes = format.BitsPerSample / 8;
        for (var i = 0; i < frames; i++)
        {
            var frame = source.AsSpan(i * format.BlockAlign, format.BlockAlign);
            var left = Sample(frame[..bytes]);
            var right = format.Channels == 1 ? left : Sample(frame.Slice(bytes, bytes));
            // Windows channel order: FL, FR, FC, LFE, BL, BR, SL, SR.
            if (channelMode == MicrophoneChannelMode.Auto && format.Channels > 2)
            {
                var center = Sample(frame.Slice(2 * bytes, bytes)) * .70710678f;
                left += center; right += center;
                for (var c = 4; c < format.Channels; c++)
                    if ((c & 1) == 0) left += Sample(frame.Slice(c * bytes, bytes)) * .70710678f;
                    else right += Sample(frame.Slice(c * bytes, bytes)) * .70710678f;
            }
            if (channelMode == MicrophoneChannelMode.Channel1) right = left;
            else if (channelMode == MicrophoneChannelMode.Channel2) left = right;
            else if (channelMode == MicrophoneChannelMode.MonoMix)
            {
                double sum = left + (double)right;
                for (var c = 2; c < format.Channels; c++) sum += Sample(frame.Slice(c * bytes, bytes));
                left = right = (float)(sum / format.Channels);
            }
            stereo[at + i * 2] = left; stereo[at + i * 2 + 1] = right;
        }
        int outputFrames = frames;
        if (resampler != null)
        {
            // Limit output by cumulative input duration. Requesting ceil(packet
            // duration) lets WDL consume past the buffer on high-rate devices.
            var capacity = checked((int)(inputFrames * AudioTrack.Rate / format.SampleRate - emittedFrames));
            var converted = new float[capacity * 2];
            outputFrames = resampler.ResampleOut(converted, 0, frames, capacity, 2);
            stereo = converted; at = 0;
        }
        var pcm = new byte[outputFrames * AudioTrack.BytesPerFrame];
        var samples = MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)Math.Clamp(Math.Round(stereo[at + i] * 32768.0), short.MinValue, short.MaxValue);
        var packet = new AudioPacket(nextOutput, pcm);
        emittedFrames += outputFrames;
        nextOutput = packet.End;
        return packet;
    }
    private float Sample(ReadOnlySpan<byte> bytes)
    {
        double value;
        if (floating) value = bytes.Length == 4 ? BitConverter.ToSingle(bytes) : BitConverter.ToDouble(bytes);
        else value = bytes.Length switch
        {
            1 => (bytes[0] - 128) / 128.0,
            2 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
            3 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608.0,
            _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0
        };
        return double.IsFinite(value) ? (float)value : 0;
    }
}

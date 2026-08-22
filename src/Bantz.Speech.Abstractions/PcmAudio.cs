namespace Bantz.Speech;

/// <summary>Normalized signed 16-bit PCM audio.</summary>
public sealed class PcmAudio
{
    /// <summary>The sample rate required by Bantz speech engines.</summary>
    public const int SpeechSampleRate = 16_000;

    /// <summary>The channel count required by Bantz speech engines.</summary>
    public const int SpeechChannels = 1;

    /// <summary>Creates a normalized PCM buffer.</summary>
    public PcmAudio(ReadOnlyMemory<byte> data, int sampleRate = SpeechSampleRate, int channels = SpeechChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        if (data.Length % (sizeof(short) * channels) != 0)
        {
            throw new ArgumentException("PCM data must contain complete signed 16-bit samples.", nameof(data));
        }

        Data = data;
        SampleRate = sampleRate;
        Channels = channels;
    }

    /// <summary>Signed little-endian 16-bit samples.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Samples per second, per channel.</summary>
    public int SampleRate { get; }

    /// <summary>Interleaved channel count.</summary>
    public int Channels { get; }

    /// <summary>The duration represented by the buffer.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(
        Data.Length / (double)(SampleRate * Channels * sizeof(short)));

    /// <summary>Creates a seekable PCM WAV stream owned by the caller.</summary>
    public Stream CreateWaveStream()
    {
        const int headerSize = 44;
        var stream = new MemoryStream(headerSize + Data.Length);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var bytesPerSecond = SampleRate * Channels * sizeof(short);
            writer.Write("RIFF"u8);
            writer.Write(36 + Data.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(bytesPerSecond);
            writer.Write((short)(Channels * sizeof(short)));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(Data.Length);
            writer.Write(Data.Span);
        }

        stream.Position = 0;
        return stream;
    }
}

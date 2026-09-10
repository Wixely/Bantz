using System.Buffers.Binary;
using System.Text;
using Bantz.Speech;
using Xunit;

namespace Bantz.Packages.Tests;

public class WaveHeaderTests
{
    /// <summary>The reader has to agree with the writer sitting next to it.</summary>
    [Fact]
    public void ItReadsWhatCreateWaveStreamWrote()
    {
        var audio = new PcmAudio(new byte[32_000]);
        using var wave = audio.CreateWaveStream();
        var bytes = new byte[wave.Length];
        wave.ReadExactly(bytes);

        Assert.True(WaveHeader.TryParse(bytes, out var format, out var dataOffset));

        Assert.Equal(PcmAudio.SpeechSampleRate, format.SampleRate);
        Assert.Equal(PcmAudio.SpeechChannels, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
        Assert.Equal(44, dataOffset);
        Assert.Equal(audio.Data.Length, bytes.Length - dataOffset);
    }

    /// <summary>
    /// The rate has to come from the header rather than from what the caller expected, which is the
    /// whole reason to read one.
    /// </summary>
    [Theory]
    [InlineData(8_000, 1, 16)]
    [InlineData(22_050, 1, 16)]
    [InlineData(24_000, 2, 16)]
    [InlineData(48_000, 2, 24)]
    public void ItReportsTheDeclaredFormat(int sampleRate, int channels, int bitsPerSample)
    {
        var wave = Wave(sampleRate, channels, bitsPerSample);

        Assert.True(WaveHeader.TryParse(wave, out var format, out _));

        Assert.Equal(new WaveFormat(sampleRate, channels, bitsPerSample), format);
    }

    /// <summary>
    /// Servers interpose LIST and fact chunks, and a reader that trusts the canonical 44-byte
    /// layout plays that metadata as audio.
    /// </summary>
    [Fact]
    public void ItWalksPastChunksSittingBeforeTheData()
    {
        var wave = Wave(24_000, 1, 16, before:
        [
            Chunk("LIST", Encoding.ASCII.GetBytes("INFOISFTLavf60")),
            Chunk("fact", [0x10, 0x00, 0x00, 0x00]),
        ]);

        Assert.True(WaveHeader.TryParse(wave, out var format, out var dataOffset));

        Assert.Equal(24_000, format.SampleRate);
        Assert.Equal("data", Encoding.ASCII.GetString(wave, dataOffset - 8, 4));
        Assert.Equal((byte)0xAB, wave[dataOffset]);
    }

    /// <summary>An odd-sized chunk is followed by a pad byte that is not part of it.</summary>
    [Fact]
    public void ItAccountsForThePadByteAfterAnOddSizedChunk()
    {
        var wave = Wave(16_000, 1, 16, before: [Chunk("LIST", Encoding.ASCII.GetBytes("odd"))]);

        Assert.True(WaveHeader.TryParse(wave, out _, out var dataOffset));

        Assert.Equal((byte)0xAB, wave[dataOffset]);
    }

    /// <summary>
    /// A server streaming a synthesis cannot know the length in advance and writes 0 or
    /// 0xFFFFFFFF; the stream ending is what ends the audio.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(0xFFFFFFFFu)]
    public void ItIgnoresTheDeclaredSizeOfAStreamedDataChunk(uint declaredSize)
    {
        var wave = Wave(24_000, 1, 16, dataSize: declaredSize, riffSize: declaredSize);

        Assert.True(WaveHeader.TryParse(wave, out var format, out var dataOffset));

        Assert.Equal(24_000, format.SampleRate);
        Assert.Equal((byte)0xAB, wave[dataOffset]);
    }

    /// <summary>The header alone is enough; the samples do not have to have arrived yet.</summary>
    [Fact]
    public void ItParsesAHeaderThatStopsAtTheDataChunk()
    {
        var wave = Wave(16_000, 1, 16);
        var headerOnly = wave.AsSpan(0, 44);

        Assert.True(WaveHeader.TryParse(headerOnly, out var format, out var dataOffset));

        Assert.Equal(16_000, format.SampleRate);
        Assert.Equal(44, dataOffset);
    }

    [Fact]
    public void ItRejectsAStreamThatIsNotRiffWave()
    {
        Assert.False(WaveHeader.TryParse(Encoding.ASCII.GetBytes("ID3ffff"), out _, out _));
        Assert.False(WaveHeader.TryParse([], out _, out _));
        Assert.False(WaveHeader.TryParse(Wave(16_000, 1, 16).AsSpan(0, 8), out _, out _));

        var notWave = Wave(16_000, 1, 16);
        Encoding.ASCII.GetBytes("AVI ").CopyTo(notWave, 8);
        Assert.False(WaveHeader.TryParse(notWave, out _, out _));
    }

    /// <summary>
    /// Samples that are not integers must not be handed back as though they were: a caller
    /// expecting PCM would read float samples as though they were 16-bit integers.
    /// </summary>
    [Fact]
    public void ItRejectsAnEncodingThatIsNotIntegerPcm()
    {
        var floatWave = Wave(24_000, 1, 32, formatTag: 3);

        Assert.False(WaveHeader.TryParse(floatWave, out _, out _));
    }

    [Fact]
    public void ItRejectsDataDeclaredBeforeAFormat()
    {
        var wave = Wave(16_000, 1, 16);
        // Rename the fmt chunk so `data` is reached without a format having been read.
        WriteId("junk", wave, 12);

        Assert.False(WaveHeader.TryParse(wave, out _, out _));
    }

    [Fact]
    public void ItRejectsAMalformedFormatChunk()
    {
        var shortFormat = Wave(16_000, 1, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(shortFormat.AsSpan(16), 8);
        Assert.False(WaveHeader.TryParse(shortFormat, out _, out _));

        foreach (var (offset, value) in new[] { (22, 0), (34, 0) })
        {
            var broken = Wave(16_000, 1, 16);
            BinaryPrimitives.WriteUInt16LittleEndian(broken.AsSpan(offset), (ushort)value);
            Assert.False(WaveHeader.TryParse(broken, out _, out _));
        }

        var zeroRate = Wave(16_000, 1, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(zeroRate.AsSpan(24), 0);
        Assert.False(WaveHeader.TryParse(zeroRate, out _, out _));
    }

    /// <summary>A chunk whose size runs past the buffer is a truncated stream, not a format.</summary>
    [Fact]
    public void ItRejectsAChunkThatOverrunsTheBuffer()
    {
        var wave = Wave(16_000, 1, 16, before: [Chunk("LIST", Encoding.ASCII.GetBytes("meta"))]);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), uint.MaxValue);

        Assert.False(WaveHeader.TryParse(wave, out _, out _));
    }

    private static void WriteId(string id, byte[] target, int offset) =>
        Encoding.ASCII.GetBytes(id).CopyTo(target, offset);

    private static byte[] Chunk(string id, byte[] payload)
    {
        var chunk = new byte[8 + payload.Length + (payload.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(chunk, 8);
        return chunk;
    }

    /// <summary>A WAV stream built the way a server would, rather than the canonical 44 bytes.</summary>
    private static byte[] Wave(
        int sampleRate,
        int channels,
        int bitsPerSample,
        ushort formatTag = 1,
        uint? dataSize = null,
        uint? riffSize = null,
        byte[][]? before = null)
    {
        var samples = new byte[64];
        Array.Fill(samples, (byte)0xAB);

        var format = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(format, formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(8), (uint)(sampleRate * channels * bitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12), (ushort)(channels * bitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14), (ushort)bitsPerSample);

        var body = new List<byte>();
        body.AddRange(Chunk("fmt ", format));
        foreach (var chunk in before ?? [])
        {
            body.AddRange(chunk);
        }

        var data = new byte[8];
        Encoding.ASCII.GetBytes("data").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), dataSize ?? (uint)samples.Length);
        body.AddRange(data);
        body.AddRange(samples);

        var wave = new byte[12 + body.Count];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), riffSize ?? (uint)(4 + body.Count));
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wave, 8);
        body.CopyTo(wave, 12);
        return wave;
    }
}

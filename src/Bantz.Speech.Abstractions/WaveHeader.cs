using System.Buffers.Binary;

namespace Bantz.Speech;

/// <summary>The format a RIFF/WAVE header declares.</summary>
public readonly record struct WaveFormat(int SampleRate, int Channels, int BitsPerSample);

/// <summary>
/// Reads the RIFF/WAVE header that <see cref="PcmAudio.CreateWaveStream"/> writes, for audio
/// arriving from somewhere else — a speech server replying with <c>response_format: wav</c>, say,
/// where the header is the only place the sample rate is stated. Taking a configured rate instead
/// of the header's plays everything back at the wrong pitch.
/// </summary>
public static class WaveHeader
{
    private const ushort PcmFormatTag = 1;
    private const ushort ExtensibleFormatTag = 0xFFFE;
    private const int RiffHeaderLength = 12;
    private const int ChunkHeaderLength = 8;

    /// <summary>
    /// Reads the format and locates the sample data.
    /// </summary>
    /// <param name="header">The start of the stream. It needs to reach the <c>data</c> chunk
    /// header, not to contain the samples.</param>
    /// <param name="format">The declared format, when this returns true.</param>
    /// <param name="dataOffset">The offset of the first sample byte, when this returns true.</param>
    /// <returns>
    /// False for anything that is not a RIFF/WAVE stream carrying integer PCM: a truncated header,
    /// a missing or malformed <c>fmt </c> chunk, sample data declared before the format, or an
    /// encoding whose samples are not integers (IEEE float among them), which a caller expecting
    /// PCM must not be handed as though it were.
    /// </returns>
    public static bool TryParse(ReadOnlySpan<byte> header, out WaveFormat format, out int dataOffset)
    {
        format = default;
        dataOffset = 0;

        if (header.Length < RiffHeaderLength ||
            !header[..4].SequenceEqual("RIFF"u8) ||
            !header[8..RiffHeaderLength].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        // The RIFF chunk's own size is not consulted: a server streaming a synthesis cannot know
        // the length in advance and writes 0 or 0xFFFFFFFF there, exactly as it does for `data`.
        var parsed = false;
        var offset = RiffHeaderLength;
        while (offset + ChunkHeaderLength <= header.Length)
        {
            var id = header.Slice(offset, 4);
            var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(offset + 4, 4));
            var payload = offset + ChunkHeaderLength;

            if (id.SequenceEqual("data"u8))
            {
                // The declared size is deliberately ignored — see above. The stream ending is what
                // ends the audio.
                if (!parsed)
                {
                    return false;
                }

                dataOffset = payload;
                return true;
            }

            if (id.SequenceEqual("fmt "u8))
            {
                if (!TryReadFormat(header[payload..], declaredSize, out format))
                {
                    return false;
                }

                parsed = true;
            }

            // Chunks are word-aligned: an odd-sized one is followed by a pad byte. Walking the list
            // rather than assuming the canonical 44-byte layout is what makes this work on the
            // streams that interpose LIST and fact chunks — a reader that trusts the offset plays
            // that metadata as audio.
            var next = (long)payload + declaredSize + (declaredSize & 1);
            if (next > header.Length || next <= offset)
            {
                return false;
            }

            offset = (int)next;
        }

        return false;
    }

    private static bool TryReadFormat(ReadOnlySpan<byte> payload, uint declaredSize, out WaveFormat format)
    {
        format = default;
        const int minimumFormatSize = 16;
        if (declaredSize < minimumFormatSize || payload.Length < minimumFormatSize)
        {
            return false;
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (tag is not (PcmFormatTag or ExtensibleFormatTag))
        {
            return false;
        }

        var channels = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);

        if (channels == 0 || sampleRate is 0 or > int.MaxValue || bitsPerSample == 0 || bitsPerSample % 8 != 0)
        {
            return false;
        }

        format = new WaveFormat((int)sampleRate, channels, bitsPerSample);
        return true;
    }
}

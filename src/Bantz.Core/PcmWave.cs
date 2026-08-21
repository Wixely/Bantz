namespace Bantz.Core;

public static class PcmWave
{
    public static MemoryStream CreateStream(
        byte[] pcm,
        int sampleRate = 16_000,
        short channels = 1,
        short bitsPerSample = 16)
    {
        var stream = new MemoryStream(capacity: checked(pcm.Length + 44));
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var blockAlign = checked((short)(channels * bitsPerSample / 8));
            var bytesPerSecond = checked(sampleRate * blockAlign);
            writer.Write("RIFF"u8);
            writer.Write(checked(36 + pcm.Length));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(bytesPerSecond);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write("data"u8);
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }

        stream.Position = 0;
        return stream;
    }
}

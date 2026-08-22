using System.Buffers.Binary;
using Bantz.Core;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class AudioSignalAnalyzerTests
{
    [Fact]
    public void SilenceProducesMinimumVisualBarsWithoutActiveAudio()
    {
        var analyzer = new AudioSignalAnalyzer();
        AudioSignalFrame? frame = null;
        analyzer.FrameAnalyzed += value => frame = value;

        analyzer.AnalyzePcm16(new byte[32_000]);

        Assert.NotNull(frame);
        Assert.Equal(0, frame.Value.Rms);
        Assert.Equal(0.12f, frame.Value.FirstBar);
        Assert.Equal(TimeSpan.FromSeconds(1), analyzer.Summary.Duration);
        Assert.Equal(TimeSpan.Zero, analyzer.Summary.ActiveDuration);
        Assert.False(AudioSignalAnalyzer.HasMeaningfulSound(analyzer.Summary));
    }

    [Fact]
    public void AudiblePcmUpdatesBarsAndReusableActivityMetrics()
    {
        var analyzer = new AudioSignalAnalyzer();
        var pcm = new byte[32_000];
        for (var offset = 0; offset < pcm.Length; offset += sizeof(short))
        {
            var sample = offset % 8 == 0 ? (short)12_000 : (short)-12_000;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset), sample);
        }

        analyzer.AnalyzePcm16(pcm);

        Assert.True(analyzer.Summary.Peak > 0.3f);
        Assert.Equal(TimeSpan.FromSeconds(1), analyzer.Summary.ActiveDuration);
        Assert.True(AudioSignalAnalyzer.HasMeaningfulSound(analyzer.Summary));

        analyzer.Reset();

        Assert.Equal(default, analyzer.Summary);
    }
}

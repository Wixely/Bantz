using System.Buffers.Binary;

namespace Bantz.Core;

public readonly record struct AudioSignalFrame(
    float FirstBar,
    float SecondBar,
    float ThirdBar,
    float Rms,
    float Peak);

public readonly record struct AudioSignalSummary(
    TimeSpan Duration,
    TimeSpan ActiveDuration,
    float Peak);

public sealed class AudioSignalAnalyzer
{
    public const float ActiveRmsThreshold = 0.012f;
    public const float MeaningfulPeakThreshold = 0.025f;
    public static readonly TimeSpan MinimumMeaningfulActivity = TimeSpan.FromMilliseconds(150);
    private const int BarCount = 3;
    private const float MinimumVisualLevel = 0.12f;
    private const float VisualMovementGain = 2f;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly object _sync = new();
    private readonly float[] _smoothedBars = new float[BarCount];
    private long _sampleFrames;
    private long _activeSampleFrames;
    private float _peak;

    public AudioSignalAnalyzer(int sampleRate = 16_000, int channels = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        _sampleRate = sampleRate;
        _channels = channels;
    }

    public event Action<AudioSignalFrame>? FrameAnalyzed;

    public AudioSignalSummary Summary
    {
        get
        {
            lock (_sync)
            {
                return new AudioSignalSummary(
                    TimeSpan.FromSeconds((double)_sampleFrames / _sampleRate),
                    TimeSpan.FromSeconds((double)_activeSampleFrames / _sampleRate),
                    _peak);
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _sampleFrames = 0;
            _activeSampleFrames = 0;
            _peak = 0;
            Array.Clear(_smoothedBars);
        }
    }

    public void AnalyzePcm16(ReadOnlySpan<byte> pcmBytes)
    {
        var sampleCount = pcmBytes.Length / sizeof(short);
        var sampleFrameCount = sampleCount / _channels;
        if (sampleFrameCount == 0)
        {
            return;
        }

        Span<double> barSquares = stackalloc double[BarCount];
        Span<int> barSamples = stackalloc int[BarCount];
        double totalSquares = 0;
        float peak = 0;
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var offset = sampleIndex * sizeof(short);
            var normalized = BinaryPrimitives.ReadInt16LittleEndian(pcmBytes[offset..]) / 32768f;
            var magnitude = MathF.Abs(normalized);
            peak = MathF.Max(peak, magnitude);
            var square = (double)normalized * normalized;
            totalSquares += square;
            var frameIndex = sampleIndex / _channels;
            var bar = Math.Min(BarCount - 1, frameIndex * BarCount / sampleFrameCount);
            barSquares[bar] += square;
            barSamples[bar]++;
        }

        var rms = (float)Math.Sqrt(totalSquares / sampleCount);
        AudioSignalFrame frame;
        lock (_sync)
        {
            _sampleFrames += sampleFrameCount;
            if (rms >= ActiveRmsThreshold)
            {
                _activeSampleFrames += sampleFrameCount;
            }

            _peak = MathF.Max(_peak, peak);
            for (var bar = 0; bar < BarCount; bar++)
            {
                var barRms = barSamples[bar] == 0
                    ? 0
                    : (float)Math.Sqrt(barSquares[bar] / barSamples[bar]);
                var visualLevel = ToVisualLevel(barRms);
                _smoothedBars[bar] = MathF.Max(visualLevel, _smoothedBars[bar] * 0.62f);
            }

            frame = new AudioSignalFrame(
                _smoothedBars[0],
                _smoothedBars[1],
                _smoothedBars[2],
                rms,
                peak);
        }

        FrameAnalyzed?.Invoke(frame);
    }

    public static bool HasMeaningfulSound(AudioSignalSummary summary) =>
        summary.Duration > TimeSpan.Zero &&
        summary.ActiveDuration >= MinimumMeaningfulActivity &&
        summary.Peak >= MeaningfulPeakThreshold;

    private static float ToVisualLevel(float rms)
    {
        if (rms <= 0.0001f)
        {
            return MinimumVisualLevel;
        }

        var decibels = 20 * MathF.Log10(rms);
        var visualLevel = Math.Clamp((decibels + 52) / 42, MinimumVisualLevel, 1f);
        return Math.Clamp(
            MinimumVisualLevel + ((visualLevel - MinimumVisualLevel) * VisualMovementGain),
            MinimumVisualLevel,
            1f);
    }
}

using System.Text;
using Bantz.Capture;
using Bantz.Input;
using Bantz.Speech;
using Bantz.Speech.Whisper;
using Xunit;

namespace Bantz.Packages.Tests;

public sealed class PackageContractTests
{
    [Fact]
    public void SpeechAbstractionsDoNotReferenceWhisper()
    {
        var references = typeof(ITranscriptionEngine).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name?.Contains("Whisper", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task AConsumerCanImplementAnEngineWithoutWhisperTypes()
    {
        ITranscriptionEngine engine = new FakeEngine();
        var result = await engine.TranscribeAsync(new PcmAudio(new byte[320]));
        Assert.Equal("consumer engine", result.Text);
        Assert.True(engine.IsReady);
    }

    [Fact]
    public void PcmAudioCreatesASeekableWaveWithCorrectDuration()
    {
        var audio = new PcmAudio(new byte[32_000]);
        using var wave = audio.CreateWaveStream();
        Span<byte> header = stackalloc byte[12];
        Assert.Equal(header.Length, wave.Read(header));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(header[..4]));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(header[8..]));
        Assert.Equal(TimeSpan.FromSeconds(1), audio.Duration);
    }

    [Fact]
    public void PackageOptionsExposeConsumerOwnedStorageAndDeviceSeams()
    {
        var capture = new AudioCaptureOptions("7");
        var whisper = new WhisperOptions
        {
            ModelPathProvider = () => "models/custom.bin",
            RuntimeRootProvider = () => "runtimes",
            Runtime = TranscriptionRuntime.Cpu,
        };

        Assert.Equal("7", capture.DeviceId);
        Assert.Equal("models/custom.bin", whisper.ModelPathProvider());
        Assert.Equal(TranscriptionRuntime.Cpu, whisper.Runtime);
    }

    [Fact]
    public void GlobalInputCapabilitiesAreHonestForCurrentPlatform()
    {
        Assert.Equal(OperatingSystem.IsWindows(), GlobalInputCapabilities.Current.SupportsGlobalBindings);
    }

    [Fact]
    public async Task InstallationLockSerializesConcurrentInstallers()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bantz-package-lock-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "install.lock");
        try
        {
            await using (var first = await FileInstallationLock.AcquireAsync(path))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await FileInstallationLock.AcquireAsync(path, timeout.Token));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FakeEngine : ITranscriptionEngine
    {
        public Task<TranscriptionResult> TranscribeAsync(PcmAudio audio, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranscriptionResult("consumer engine"));
    }
}

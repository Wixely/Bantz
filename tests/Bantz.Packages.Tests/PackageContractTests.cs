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
    public void DeviceEnumerationAlwaysOffersTheSystemDefaultFirst()
    {
        var devices = AudioCaptureDevices.List();

        Assert.NotEmpty(devices);
        Assert.Equal(AudioCaptureDevices.DefaultId, devices[0].Id);
        Assert.All(devices, device => Assert.False(string.IsNullOrWhiteSpace(device.Name)));
    }

    [Fact]
    public void AlsaListingsBecomeSelectableDevices()
    {
        const string listing = @"null
    Discard all samples
default
    Default Audio Device
sysdefault:CARD=PCH
    HDA Intel PCH, ALC257 Analog
    Default Audio Device
plughw:CARD=PCH,DEV=0
";

        var devices = AudioCaptureDevices.ParseAlsaDevices(listing);

        Assert.Equal(AudioCaptureDevices.DefaultId, devices[0].Id);
        Assert.DoesNotContain(devices, device => device.Id == "null");
        Assert.Equal(3, devices.Length);
        Assert.Equal("sysdefault:CARD=PCH", devices[1].Id);
        Assert.Equal("HDA Intel PCH, ALC257 Analog", devices[1].Name);
        Assert.Equal("plughw:CARD=PCH,DEV=0", devices[2].Id);
        Assert.Equal("plughw:CARD=PCH,DEV=0", devices[2].Name);
    }

    [Fact]
    public void TruncatedWaveInNamesAreCompletedFromTheAudioEndpoints()
    {
        // Wave-in cuts names to 31 characters; Core Audio reports them in full.
        string[] endpoints =
        [
            "Microphone (Steam Streaming Microphone)",
            "Microphone (SteelSeries Arctis 7 Chat)",
        ];
        var claimed = new bool[endpoints.Length];

        var arctis = AudioCaptureDevices.ExpandDeviceName("Microphone (SteelSeries Arctis", 0, endpoints, claimed);
        var steam = AudioCaptureDevices.ExpandDeviceName("Microphone (Steam Streaming Mic", 1, endpoints, claimed);

        Assert.Equal("Microphone (SteelSeries Arctis 7 Chat)", arctis);
        Assert.Equal("Microphone (Steam Streaming Microphone)", steam);
    }

    [Fact]
    public void IdenticalMicrophonesClaimSeparateEndpointNames()
    {
        string[] endpoints = ["Microphone (USB Audio)", "Microphone (USB Audio)"];
        var claimed = new bool[endpoints.Length];

        var first = AudioCaptureDevices.ExpandDeviceName("Microphone (USB Audio)", 0, endpoints, claimed);
        var second = AudioCaptureDevices.ExpandDeviceName("Microphone (USB Audio)", 1, endpoints, claimed);

        Assert.Equal("Microphone (USB Audio)", first);
        Assert.Equal("Microphone (USB Audio)", second);
        Assert.All(claimed, Assert.True);
    }

    [Fact]
    public void AWaveInNameSurvivesWhenNoEndpointMatchesIt()
    {
        string[] endpoints = ["Microphone (Something Else)"];
        var claimed = new bool[endpoints.Length];

        Assert.Equal("Line In (Realtek)", AudioCaptureDevices.ExpandDeviceName("Line In (Realtek)", 2, endpoints, claimed));
        Assert.Equal("Microphone 3", AudioCaptureDevices.ExpandDeviceName("", 3, endpoints, claimed));
        Assert.All(claimed, Assert.False);
    }

    [Fact]
    public void ARecorderCanReadItsDeviceWhenEachSessionStarts()
    {
        var deviceId = "1";
        using var recorder = new WindowsAudioRecorder(null, () => new AudioCaptureOptions(deviceId));

        Assert.NotNull(recorder);
        Assert.Throws<ArgumentNullException>(() => new WindowsAudioRecorder(null, (Func<AudioCaptureOptions>)null!));
    }

    [Fact]
    public void EveryCatalogueModelIsDistinctAndDescribed()
    {
        var models = WhisperModelCatalog.All;

        Assert.NotEmpty(models);
        Assert.Equal(models.Count, models.Select(model => model.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(models.Count, models.Select(model => model.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(models, model =>
        {
            Assert.False(string.IsNullOrWhiteSpace(model.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(model.Summary));
            Assert.True(model.DownloadBytes > 0);
            Assert.StartsWith("ggml-", model.FileName, StringComparison.Ordinal);
        });
        Assert.Equal(WhisperModelCatalog.DefaultModelId, WhisperModelCatalog.Default.Id);
    }

    [Fact]
    public void OnlyTheDefaultModelShipsWithAPinnedHash()
    {
        // The rest are checked for the ggml header and the length the server declares, because
        // Bantz does not publish a hash for every model.
        var pinned = WhisperModelCatalog.All.Where(model => model.HasPinnedIntegrity).ToArray();

        var only = Assert.Single(pinned);
        Assert.Equal(WhisperModelCatalog.DefaultModelId, only.Id);
        Assert.Equal(147_964_211, only.ExactBytes);
    }

    [Fact]
    public void EnglishOnlyModelsRefuseOtherLanguages()
    {
        var english = WhisperModelCatalog.Resolve("base.en");
        var multilingual = WhisperModelCatalog.Resolve("base");

        Assert.Equal("en", SpeechLanguages.Resolve("fr", english));
        Assert.Equal("fr", SpeechLanguages.Resolve("fr", multilingual));
        Assert.Equal("auto", SpeechLanguages.Resolve("auto", multilingual));
        Assert.Equal("en", SpeechLanguages.Resolve("klingon", multilingual));
        Assert.Equal("en", SpeechLanguages.Resolve(null, multilingual));
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

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Bantz.Speech;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace Bantz.Speech.Whisper;

public sealed class WhisperTranscriptionEngine : ITranscriptionEngine, IDisposable
{
    public const long BaseEnglishModelBytes = 147_964_211;
    public const string BaseEnglishModelSha256 = "A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002";
    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private readonly Func<string> _modelPathProvider;
    private readonly string _language = "en";
    private string _lastRun = "Not run yet";

    public WhisperTranscriptionEngine(string modelPath)
        : this(() => modelPath)
    {
    }

    public WhisperTranscriptionEngine(Func<string> modelPathProvider)
    {
        _modelPathProvider = modelPathProvider;
    }

    public WhisperTranscriptionEngine(WhisperOptions options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).ModelPathProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Language);
        _language = options.Language;
        ConfigureRuntime(options.Runtime, new WhisperRuntimeManager(options.RuntimeRootProvider));
    }

    private string ModelPath => _modelPathProvider();
    public bool IsModelAvailable => File.Exists(ModelPath);
    public bool IsReady => IsModelAvailable;

    public async ValueTask InitializeAsync(
        IProgress<TranscriptionInitializationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IProgress<ModelDownloadProgress>? modelProgress = progress is null
            ? null
            : new Progress<ModelDownloadProgress>(value => progress.Report(new(
                TranscriptionInitializationStage.DownloadingModel,
                value.DownloadedBytes,
                value.TotalBytes)));
        await DownloadModelAsync(modelProgress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new TranscriptionInitializationProgress(TranscriptionInitializationStage.Ready, 1, 1));
    }

    public void ProbeRuntime()
    {
        using var factory = WhisperFactory.FromPath(ModelPath);
    }

    public static void ConfigureRuntime(TranscriptionRuntime runtime, WhisperRuntimeManager runtimeManager)
    {
        if (RuntimeOptions.LoadedLibrary is not null)
        {
            return;
        }

        RuntimeOptions.LibraryPath = runtimeManager.LoaderSearchAnchor;
        RuntimeOptions.RuntimeLibraryOrder = runtime == TranscriptionRuntime.Cpu
            ? [RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Vulkan];
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        PcmAudio audio,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await EnsureModelAsync(cancellationToken).ConfigureAwait(false);
            if (audio.SampleRate != PcmAudio.SpeechSampleRate || audio.Channels != PcmAudio.SpeechChannels)
            {
                throw new ArgumentException("Whisper requires signed 16-bit, 16 kHz, mono PCM.", nameof(audio));
            }

            using var waveAudio = audio.CreateWaveStream();

            using var factory = WhisperFactory.FromPath(ModelPath);
            using var processor = factory.CreateBuilder()
                .WithLanguage(_language)
                .Build();
            var transcript = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(waveAudio, cancellationToken).ConfigureAwait(false))
            {
                transcript.Append(segment.Text);
            }

            _lastRun = $"Succeeded in {stopwatch.ElapsedMilliseconds:N0} ms · {transcript.Length:N0} characters";
            return new TranscriptionResult(transcript.ToString(), _language);
        }
        catch
        {
            _lastRun = $"Failed after {stopwatch.ElapsedMilliseconds:N0} ms";
            throw;
        }
    }

    public WhisperDiagnostics GetDiagnostics()
    {
        var file = new FileInfo(ModelPath);
        var available = file.Exists;
        var version = typeof(WhisperFactory).Assembly.GetName().Version?.ToString() ?? "Unknown";
        var runtime = RuntimeOptions.LoadedLibrary?.ToString() ?? "Not loaded yet";
        return new WhisperDiagnostics(
            available ? "Ready" : "Model download required",
            available ? "The local model is available and ready for transcription." : "The model will be downloaded on the first transcription.",
            "whisper.cpp via Whisper.net",
            version,
            runtime,
            _language,
            file.Name,
            available ? $"{file.Length / 1_048_576d:N1} MiB" : "Not downloaded",
            file.FullName,
            _lastRun,
            available ? "healthy" : "attention");
    }

    TranscriptionDiagnostics ITranscriptionEngine.GetDiagnostics()
    {
        var diagnostics = GetDiagnostics();
        return new TranscriptionDiagnostics(
            IsReady,
            diagnostics.Engine,
            diagnostics.Version,
            diagnostics.Runtime,
            diagnostics.Language,
            diagnostics.Model,
            diagnostics.ModelPath,
            diagnostics.LastRun);
    }

    private async Task EnsureModelAsync(CancellationToken cancellationToken)
    {
        await DownloadModelAsync(progress: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadModelAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(ModelPath))
        {
            progress?.Report(new ModelDownloadProgress(BaseEnglishModelBytes, BaseEnglishModelBytes));
            return;
        }

        await _modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modelPath = ModelPath;
            var directory = Path.GetDirectoryName(modelPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var installationLock = await FileInstallationLock
                .AcquireAsync(modelPath + ".lock", cancellationToken)
                .ConfigureAwait(false);
            if (File.Exists(modelPath))
            {
                return;
            }

            var temporaryPath = modelPath + ".download";
            await using (var source = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(GgmlType.BaseEn, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                useAsync: true))
            {
                var buffer = new byte[81_920];
                long downloaded = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;
                    progress?.Report(new ModelDownloadProgress(downloaded, BaseEnglishModelBytes));
                }
            }

            if (new FileInfo(temporaryPath).Length != BaseEnglishModelBytes)
            {
                File.Delete(temporaryPath);
                throw new InvalidDataException("The downloaded speech model has an unexpected size.");
            }

            string actualHash;
            await using (var modelStream = File.OpenRead(temporaryPath))
            {
                actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(modelStream, cancellationToken).ConfigureAwait(false));
            }

            if (!string.Equals(actualHash, BaseEnglishModelSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporaryPath);
                throw new InvalidDataException("The downloaded speech model failed its integrity check.");
            }

            File.Move(temporaryPath, modelPath, overwrite: true);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    public void Dispose() => _modelLock.Dispose();
}

public sealed record ModelDownloadProgress(long DownloadedBytes, long TotalBytes)
{
    public int Percent => TotalBytes <= 0
        ? 0
        : (int)Math.Clamp(DownloadedBytes * 100 / TotalBytes, 0, 100);
}

public sealed record WhisperDiagnostics(
    string Status,
    string Summary,
    string Engine,
    string Version,
    string Runtime,
    string Language,
    string Model,
    string ModelSize,
    string ModelPath,
    string LastRun,
    string StateClass);

public static class ModelPath
{
    public static string? Override(string[] args)
    {
        var argument = args
            .SkipWhile(value => !string.Equals(value, "--model", StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(argument))
        {
            return Path.GetFullPath(argument);
        }

        var environment = Environment.GetEnvironmentVariable("BANTZ_STT_MODEL");
        return string.IsNullOrWhiteSpace(environment) ? null : Path.GetFullPath(environment);
    }

    public static string Resolve(string[] args, string defaultPath)
    {
        return Override(args) ?? defaultPath;
    }
}

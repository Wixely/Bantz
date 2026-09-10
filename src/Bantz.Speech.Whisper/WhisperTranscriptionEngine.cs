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
    private readonly Func<WhisperModel> _modelProvider = static () => WhisperModelCatalog.Default;
    private readonly Func<string> _languageProvider = static () => "en";
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
        : this(ResolveModelPathProvider(options ?? throw new ArgumentNullException(nameof(options))))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Language);
        var language = options.Language;
        _languageProvider = options.LanguageProvider ?? (() => language);
        _modelProvider = options.ModelProvider ?? (static () => WhisperModelCatalog.Default);
        ConfigureRuntime(options.Runtime, new WhisperRuntimeManager(options.RuntimeRootProvider));
    }

    private static Func<string> ResolveModelPathProvider(WhisperOptions options)
    {
        if (options.ModelProvider is null || options.ModelsRootProvider is null)
        {
            return options.ModelPathProvider;
        }

        var models = options.ModelsRootProvider;
        var model = options.ModelProvider;
        return () => Path.Combine(models(), model().FileName);
    }

    /// <summary>The model each transcription will use.</summary>
    public WhisperModel Model => _modelProvider();

    /// <summary>The language each transcription will ask for, honouring English-only models.</summary>
    public string Language => SpeechLanguages.Resolve(_languageProvider(), Model);

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

            var language = Language;
            using var factory = WhisperFactory.FromPath(ModelPath);
            using var processor = factory.CreateBuilder()
                .WithLanguage(language)
                .Build();
            var transcript = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(waveAudio, cancellationToken).ConfigureAwait(false))
            {
                transcript.Append(segment.Text);
            }

            _lastRun = $"Succeeded in {stopwatch.ElapsedMilliseconds:N0} ms · {transcript.Length:N0} characters";
            return new TranscriptionResult(transcript.ToString(), language);
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
            $"{LanguageDescription} · {Model.DisplayName}",
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

    private string LanguageDescription
    {
        get
        {
            var requested = _languageProvider();
            var resolved = Language;
            var name = SpeechLanguages.Find(resolved)?.Name ?? resolved;
            return Model.IsMultilingual &&
                string.Equals(requested, WhisperModelCatalog.AutomaticLanguage, StringComparison.OrdinalIgnoreCase)
                ? "Detected automatically"
                : $"{name} ({resolved})";
        }
    }

    private async Task EnsureModelAsync(CancellationToken cancellationToken)
    {
        await DownloadModelAsync(progress: null, cancellationToken).ConfigureAwait(false);
    }

    public Task DownloadModelAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken = default) =>
        DownloadModelAsync(Model, progress, cancellationToken);

    /// <summary>
    /// Downloads one model into the models folder. A model Bantz has pinned is checked against its
    /// published size and hash; for the rest, the download is checked for the ggml header and for
    /// matching the length the server declared, which is what can be verified without shipping a
    /// hash for every model.
    /// </summary>
    public async Task DownloadModelAsync(
        WhisperModel model,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var modelPath = PathFor(model);
        if (File.Exists(modelPath))
        {
            var installed = new FileInfo(modelPath).Length;
            progress?.Report(new ModelDownloadProgress(installed, installed));
            return;
        }

        await _modelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
            var expectedBytes = model.DownloadBytes;
            await using (var source = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(model.GgmlType, cancellationToken: cancellationToken)
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
                    progress?.Report(new ModelDownloadProgress(downloaded, Math.Max(expectedBytes, downloaded)));
                }
            }

            await VerifyDownloadAsync(model, temporaryPath, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, modelPath, overwrite: true);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private static async Task VerifyDownloadAsync(
        WhisperModel model,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        var length = new FileInfo(temporaryPath).Length;
        if (model.HasPinnedIntegrity)
        {
            if (length != model.ExactBytes)
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

            if (!string.Equals(actualHash, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporaryPath);
                throw new InvalidDataException("The downloaded speech model failed its integrity check.");
            }

            return;
        }

        // No published hash to compare against, so check the file is the ggml container Whisper
        // expects and is not a truncated or error-page download.
        if (length < MinimumModelBytes || !await HasGgmlHeaderAsync(temporaryPath, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(temporaryPath);
            throw new InvalidDataException("The downloaded speech model is not a usable Whisper model file.");
        }
    }

    private static async Task<bool> HasGgmlHeaderAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await using var stream = File.OpenRead(path);
        return await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) == header.Length &&
            GgmlMagic.AsSpan().SequenceEqual(header);
    }

    /// <summary>Smaller than any published Whisper model, so anything below this is a bad download.</summary>
    private const long MinimumModelBytes = 10 * 1024 * 1024;

    /// <summary>The 'ggml' magic every Whisper model file starts with.</summary>
    private static readonly byte[] GgmlMagic = [0x6C, 0x6D, 0x67, 0x67];

    /// <summary>The path a model occupies, downloaded or not.</summary>
    public string PathFor(WhisperModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var directory = Path.GetDirectoryName(ModelPath);
        return string.IsNullOrEmpty(directory) ? model.FileName : Path.Combine(directory, model.FileName);
    }

    /// <summary>Whether a model is already downloaded.</summary>
    public bool IsInstalled(WhisperModel model) => File.Exists(PathFor(model));

    /// <summary>Deletes a downloaded model, reporting whether there was one to delete.</summary>
    public bool DeleteModel(WhisperModel model)
    {
        var path = PathFor(model);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
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

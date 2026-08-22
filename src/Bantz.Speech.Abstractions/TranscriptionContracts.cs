namespace Bantz.Speech;

/// <summary>A transcription result with room for future metadata.</summary>
public sealed record TranscriptionResult(string Text, string? Language = null);

/// <summary>A long-running engine initialization stage.</summary>
public enum TranscriptionInitializationStage
{
    Preparing,
    DownloadingRuntime,
    DownloadingModel,
    LoadingModel,
    Ready,
}

/// <summary>Progress reported while preparing a transcription engine.</summary>
public sealed record TranscriptionInitializationProgress(
    TranscriptionInitializationStage Stage,
    long CompletedBytes = 0,
    long TotalBytes = 0)
{
    public int Percent => TotalBytes <= 0
        ? 0
        : (int)Math.Clamp(CompletedBytes * 100 / TotalBytes, 0, 100);
}

/// <summary>App-neutral diagnostics for a transcription engine.</summary>
public sealed record TranscriptionDiagnostics(
    bool IsReady,
    string Engine,
    string Version,
    string Runtime,
    string Language,
    string Model,
    string ModelPath,
    string LastRun);

/// <summary>Converts normalized PCM audio into text.</summary>
public interface ITranscriptionEngine
{
    /// <summary>Whether the engine has everything needed to transcribe.</summary>
    bool IsReady => true;

    /// <summary>Downloads and loads resources required by the engine.</summary>
    ValueTask InitializeAsync(
        IProgress<TranscriptionInitializationProgress>? progress = null,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>Transcribes signed 16-bit, 16 kHz, mono PCM audio.</summary>
    Task<TranscriptionResult> TranscribeAsync(
        PcmAudio audio,
        CancellationToken cancellationToken = default);

    /// <summary>Returns diagnostics without making UI assumptions.</summary>
    TranscriptionDiagnostics GetDiagnostics() => new(
        IsReady,
        GetType().Name,
        GetType().Assembly.GetName().Version?.ToString() ?? "Unknown",
        "Not reported",
        "Not reported",
        "Not reported",
        "Not reported",
        "Not run yet");
}

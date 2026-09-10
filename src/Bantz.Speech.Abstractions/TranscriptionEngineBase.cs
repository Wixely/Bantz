namespace Bantz.Speech;

/// <summary>
/// The conveniences <see cref="ITranscriptionEngine"/> used to supply as default interface methods.
///
/// <para>They live here because a default interface method quietly absorbs a near miss. An engine
/// declaring <c>Task InitializeAsync(...)</c> where the interface declares <c>ValueTask</c> compiles
/// without a warning: the interface member is already satisfied by its own default, so the class
/// method is simply not the interface method. Every caller holding an
/// <see cref="ITranscriptionEngine"/> then gets the default no-op, and only a caller holding the
/// concrete type reaches the real one — which surfaces much later as "why is progress reporting
/// doing nothing", with a class that visibly implements the member sitting right there.</para>
///
/// <para>Derive from this to keep the conveniences. Implement <see cref="ITranscriptionEngine"/>
/// directly to be told at compile time when a signature does not match.</para>
/// </summary>
public abstract class TranscriptionEngineBase : ITranscriptionEngine
{
    /// <inheritdoc />
    public virtual bool IsReady => true;

    /// <inheritdoc />
    public virtual ValueTask InitializeAsync(
        IProgress<TranscriptionInitializationProgress>? progress = null,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public abstract Task<TranscriptionResult> TranscribeAsync(
        PcmAudio audio,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual TranscriptionDiagnostics GetDiagnostics() => new(
        IsReady,
        GetType().Name,
        GetType().Assembly.GetName().Version?.ToString() ?? "Unknown",
        "Not reported",
        "Not reported",
        "Not reported",
        "Not reported",
        "Not run yet");
}

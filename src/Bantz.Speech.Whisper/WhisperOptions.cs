namespace Bantz.Speech.Whisper;

/// <summary>The native Whisper runtime to load.</summary>
public enum TranscriptionRuntime
{
    Automatic,
    Cpu,
}

/// <summary>Configuration for a local Whisper transcription engine.</summary>
public sealed class WhisperOptions
{
    /// <summary>Path to the ggml model file.</summary>
    public Func<string> ModelPathProvider { get; init; } = DefaultModelPath;

    /// <summary>Directory used for downloaded native runtime libraries.</summary>
    public Func<string> RuntimeRootProvider { get; init; } = DefaultRuntimeRoot;

    /// <summary>Native runtime preference.</summary>
    public TranscriptionRuntime Runtime { get; init; } = TranscriptionRuntime.Automatic;

    /// <summary>Whisper language code.</summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// The language to transcribe, read for each transcription so a change applies to the next one.
    /// Overrides <see cref="Language"/> when set.
    /// </summary>
    public Func<string>? LanguageProvider { get; init; }

    /// <summary>
    /// The model to transcribe with, read for each transcription. When set, the model file is
    /// resolved inside <see cref="ModelsRootProvider"/> instead of using
    /// <see cref="ModelPathProvider"/>.
    /// </summary>
    public Func<WhisperModel>? ModelProvider { get; init; }

    /// <summary>The folder downloaded models are kept in. Used with <see cref="ModelProvider"/>.</summary>
    public Func<string>? ModelsRootProvider { get; init; }

    private static string DefaultModelPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bantz",
        "Speech",
        "models",
        "ggml-base.en.bin");

    private static string DefaultRuntimeRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bantz",
        "Speech",
        "runtimes");
}

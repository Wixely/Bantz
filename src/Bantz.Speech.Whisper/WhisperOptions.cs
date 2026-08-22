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

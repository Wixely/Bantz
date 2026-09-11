using Whisper.net.Ggml;

namespace Bantz.Speech.Whisper;

/// <summary>A Whisper model Bantz can download and transcribe with.</summary>
/// <param name="Id">Stable identifier stored in settings, matching the ggml name.</param>
/// <param name="DisplayName">The name to show when choosing a model.</param>
/// <param name="FileName">The file the model is stored as inside the models folder.</param>
/// <param name="GgmlType">The Whisper.net download identifier.</param>
/// <param name="ApproximateBytes">Roughly what the download costs, for showing before it starts.</param>
/// <param name="IsMultilingual">Whether the model transcribes languages other than English.</param>
/// <param name="Summary">One line on what this model trades away.</param>
/// <param name="ExactBytes">The published size, when Bantz has pinned it; otherwise zero.</param>
/// <param name="Sha256">The published hash, when Bantz has pinned it; otherwise null.</param>
public sealed record WhisperModel(
    string Id,
    string DisplayName,
    string FileName,
    GgmlType GgmlType,
    long ApproximateBytes,
    bool IsMultilingual,
    string Summary,
    long ExactBytes = 0,
    string? Sha256 = null)
{
    /// <summary>Whether Bantz can check this download against a known size and hash.</summary>
    public bool HasPinnedIntegrity => ExactBytes > 0 && !string.IsNullOrWhiteSpace(Sha256);

    /// <summary>The download size to show, preferring the pinned size when there is one.</summary>
    public long DownloadBytes => ExactBytes > 0 ? ExactBytes : ApproximateBytes;
}

/// <summary>The models Bantz offers, smallest first.</summary>
public static class WhisperModelCatalog
{
    private const long MiB = 1_048_576;

    /// <summary>The model a new installation starts with.</summary>
    public const string DefaultModelId = "base.en";

    /// <summary>The language value that asks Whisper to detect the language itself.</summary>
    public const string AutomaticLanguage = "auto";

    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new("tiny.en", "Tiny (English)", "ggml-tiny.en.bin", GgmlType.TinyEn,
            75 * MiB, false, "Fastest, least accurate"),
        new("tiny", "Tiny", "ggml-tiny.bin", GgmlType.Tiny,
            75 * MiB, true, "Fastest multilingual; expect mistakes"),
        new("base.en", "Base (English)", "ggml-base.en.bin", GgmlType.BaseEn,
            142 * MiB, false, "Balanced; the default",
            147_964_211, "A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002"),
        new("base", "Base", "ggml-base.bin", GgmlType.Base,
            142 * MiB, true, "Balanced multilingual"),
        new("small.en", "Small (English)", "ggml-small.en.bin", GgmlType.SmallEn,
            466 * MiB, false, "More accurate, slower"),
        new("small", "Small", "ggml-small.bin", GgmlType.Small,
            466 * MiB, true, "More accurate multilingual, slower"),
        new("medium.en", "Medium (English)", "ggml-medium.en.bin", GgmlType.MediumEn,
            1536 * MiB, false, "High accuracy, wants a fast machine"),
        new("medium", "Medium", "ggml-medium.bin", GgmlType.Medium,
            1536 * MiB, true, "High accuracy multilingual, slower"),
        new("large-v3-turbo", "Large v3 Turbo", "ggml-large-v3-turbo.bin", GgmlType.LargeV3Turbo,
            1600 * MiB, true, "Large-model accuracy, far cheaper"),
    ];

    /// <summary>The model used when settings name none, or name one that no longer exists.</summary>
    public static WhisperModel Default { get; } = Resolve(DefaultModelId);

    /// <summary>Finds a model by id, returning null when the id is unknown.</summary>
    public static WhisperModel? Find(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : All.FirstOrDefault(model => string.Equals(model.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a model by id, falling back to the default.</summary>
    public static WhisperModel Resolve(string? id) =>
        Find(id) ?? All.First(model => string.Equals(model.Id, DefaultModelId, StringComparison.Ordinal));

    /// <summary>Finds the model a stored file name belongs to.</summary>
    public static WhisperModel? FromFileName(string? fileName) => string.IsNullOrWhiteSpace(fileName)
        ? null
        : All.FirstOrDefault(model => string.Equals(model.FileName, fileName.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>A language Whisper can be asked to transcribe.</summary>
public sealed record SpeechLanguage(string Code, string Name)
{
    /// <summary>Whether this entry asks Whisper to detect the language itself.</summary>
    public bool IsAutomatic => string.Equals(Code, WhisperModelCatalog.AutomaticLanguage, StringComparison.Ordinal);
}

/// <summary>
/// Every language a multilingual Whisper model transcribes, which is the same set for all of them:
/// they share one tokenizer, so the model decides quality and speed, never which languages are
/// available. Cantonese is the exception and is left out — its token exists only in the large-v3
/// tokenizer, so offering it would be an option that silently fails on every other model.
/// </summary>
public static class SpeechLanguages
{
    public static IReadOnlyList<SpeechLanguage> All { get; } =
    [
        new(WhisperModelCatalog.AutomaticLanguage, "Detect automatically"),

        // English first because it is the default; the rest by name, which is the order a
        // person scanning the list expects.
        new("en", "English"),

        new("af", "Afrikaans"),
        new("sq", "Albanian"),
        new("am", "Amharic"),
        new("ar", "Arabic"),
        new("hy", "Armenian"),
        new("as", "Assamese"),
        new("az", "Azerbaijani"),
        new("ba", "Bashkir"),
        new("eu", "Basque"),
        new("be", "Belarusian"),
        new("bn", "Bengali"),
        new("bs", "Bosnian"),
        new("br", "Breton"),
        new("bg", "Bulgarian"),
        new("my", "Burmese"),
        new("ca", "Catalan"),
        new("zh", "Chinese"),
        new("hr", "Croatian"),
        new("cs", "Czech"),
        new("da", "Danish"),
        new("nl", "Dutch"),
        new("et", "Estonian"),
        new("fo", "Faroese"),
        new("fi", "Finnish"),
        new("fr", "French"),
        new("gl", "Galician"),
        new("ka", "Georgian"),
        new("de", "German"),
        new("el", "Greek"),
        new("gu", "Gujarati"),
        new("ht", "Haitian Creole"),
        new("ha", "Hausa"),
        new("haw", "Hawaiian"),
        new("he", "Hebrew"),
        new("hi", "Hindi"),
        new("hu", "Hungarian"),
        new("is", "Icelandic"),
        new("id", "Indonesian"),
        new("it", "Italian"),
        new("ja", "Japanese"),
        new("jw", "Javanese"),
        new("kn", "Kannada"),
        new("kk", "Kazakh"),
        new("km", "Khmer"),
        new("ko", "Korean"),
        new("lo", "Lao"),
        new("la", "Latin"),
        new("lv", "Latvian"),
        new("ln", "Lingala"),
        new("lt", "Lithuanian"),
        new("lb", "Luxembourgish"),
        new("mk", "Macedonian"),
        new("mg", "Malagasy"),
        new("ms", "Malay"),
        new("ml", "Malayalam"),
        new("mt", "Maltese"),
        new("mi", "Maori"),
        new("mr", "Marathi"),
        new("mn", "Mongolian"),
        new("ne", "Nepali"),
        new("no", "Norwegian"),
        new("nn", "Norwegian Nynorsk"),
        new("oc", "Occitan"),
        new("ps", "Pashto"),
        new("fa", "Persian"),
        new("pl", "Polish"),
        new("pt", "Portuguese"),
        new("pa", "Punjabi"),
        new("ro", "Romanian"),
        new("ru", "Russian"),
        new("sa", "Sanskrit"),
        new("sr", "Serbian"),
        new("sn", "Shona"),
        new("sd", "Sindhi"),
        new("si", "Sinhala"),
        new("sk", "Slovak"),
        new("sl", "Slovenian"),
        new("so", "Somali"),
        new("es", "Spanish"),
        new("su", "Sundanese"),
        new("sw", "Swahili"),
        new("sv", "Swedish"),
        new("tl", "Tagalog"),
        new("tg", "Tajik"),
        new("ta", "Tamil"),
        new("tt", "Tatar"),
        new("te", "Telugu"),
        new("th", "Thai"),
        new("bo", "Tibetan"),
        new("tr", "Turkish"),
        new("tk", "Turkmen"),
        new("uk", "Ukrainian"),
        new("ur", "Urdu"),
        new("uz", "Uzbek"),
        new("vi", "Vietnamese"),
        new("cy", "Welsh"),
        new("yi", "Yiddish"),
        new("yo", "Yoruba"),
    ];

    /// <summary>Finds a language by code, returning null when the code is not listed.</summary>
    public static SpeechLanguage? Find(string? code) => string.IsNullOrWhiteSpace(code)
        ? null
        : All.FirstOrDefault(language => string.Equals(language.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The language to transcribe with. English-only models ignore the setting, because asking one
    /// for another language produces English text with the wrong label on it.
    /// </summary>
    public static string Resolve(string? code, WhisperModel model) => model.IsMultilingual
        ? Find(code)?.Code ?? "en"
        : "en";
}

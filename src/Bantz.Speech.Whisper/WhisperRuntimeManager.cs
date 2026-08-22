using System.IO.Compression;
using System.Security.Cryptography;
namespace Bantz.Speech.Whisper;

public sealed class WhisperRuntimeManager
{
    private const string Version = "1.9.1";
    private static readonly HttpClient Http = new();
    private readonly Func<string> _rootProvider;

    public WhisperRuntimeManager(string? root = null)
        : this(() => root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bantz",
            "runtimes"))
    {
    }

    public WhisperRuntimeManager(Func<string> rootProvider)
    {
        _rootProvider = rootProvider;
    }

    public bool IsInstalled(TranscriptionRuntime runtime) => RequiredFiles(runtime)
        .All(file => File.Exists(Path.Combine(RuntimeDirectory(runtime), file)));

    public string LoaderSearchAnchor => Path.Combine(
        Root,
        OperatingSystem.IsWindows() ? "whisper-runtime-anchor.dll" : "whisper-runtime-anchor.so");

    public async Task DownloadAsync(
        TranscriptionRuntime runtime,
        IProgress<RuntimeDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (IsInstalled(runtime))
        {
            progress?.Report(new RuntimeDownloadProgress(1, 1));
            return;
        }

        var package = PackageFor(runtime);
        Directory.CreateDirectory(Root);
        await using var installationLock = await FileInstallationLock
            .AcquireAsync(Path.Combine(Root, $"{package.Key}.lock"), cancellationToken)
            .ConfigureAwait(false);
        if (IsInstalled(runtime))
        {
            progress?.Report(new RuntimeDownloadProgress(1, 1));
            return;
        }

        var packagePath = Path.Combine(Root, $"{package.Key}.nupkg.download");

        using (var response = await Http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? package.DownloadBytes;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, true);
            var buffer = new byte[81_920];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                progress?.Report(new RuntimeDownloadProgress(downloaded, total));
            }
        }

        await using (var packageStream = File.OpenRead(packagePath))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actualHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(packagePath);
                throw new InvalidDataException("The downloaded Whisper runtime failed its integrity check.");
            }
        }

        var runtimeDirectory = RuntimeDirectory(runtime);
        Directory.CreateDirectory(runtimeDirectory);
        using (var archive = ZipFile.OpenRead(packagePath))
        {
            foreach (var file in RequiredFiles(runtime))
            {
                var entry = archive.GetEntry($"build/{PlatformDirectory}/{file}")
                    ?? throw new InvalidDataException($"The Whisper runtime package is missing {file}.");
                var destinationPath = Path.Combine(runtimeDirectory, file);
                var temporaryPath = destinationPath + ".tmp";
                await using (var source = entry.Open())
                await using (var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, true))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
        }

        File.Delete(packagePath);
    }

    public static long DownloadBytes(TranscriptionRuntime runtime) => PackageFor(runtime).DownloadBytes;

    private string Root => Path.Combine(_rootProvider(), $"whisper.net-{Version}");

    private string RuntimeDirectory(TranscriptionRuntime runtime) => runtime == TranscriptionRuntime.Cpu
        ? Path.Combine(Root, "runtimes", PlatformDirectory)
        : Path.Combine(Root, "runtimes", "vulkan", PlatformDirectory);

    public static string PlatformDirectory => OperatingSystem.IsWindows()
        ? "win-x64"
        : OperatingSystem.IsLinux()
            ? "linux-x64"
            : throw new PlatformNotSupportedException("Bantz Whisper runtimes support Windows and Linux x64.");

    public static string[] RequiredFileNames(TranscriptionRuntime runtime)
    {
        var extension = OperatingSystem.IsWindows() ? ".dll" : ".so";
        var prefix = OperatingSystem.IsWindows() ? "" : "lib";
        return runtime == TranscriptionRuntime.Cpu
            ? [$"{prefix}ggml-base-whisper{extension}", $"{prefix}ggml-cpu-whisper{extension}", $"{prefix}ggml-whisper{extension}", $"{prefix}whisper{extension}"]
            : [$"{prefix}ggml-base-whisper{extension}", $"{prefix}ggml-cpu-whisper{extension}", $"{prefix}ggml-vulkan-whisper{extension}", $"{prefix}ggml-whisper{extension}", $"{prefix}whisper{extension}"];
    }

    private static string[] RequiredFiles(TranscriptionRuntime runtime) => RequiredFileNames(runtime);

    private static RuntimePackage PackageFor(TranscriptionRuntime runtime) => runtime == TranscriptionRuntime.Cpu
        ? new(
            "cpu",
            "https://api.nuget.org/v3-flatcontainer/whisper.net.runtime/1.9.1/whisper.net.runtime.1.9.1.nupkg",
            "B5224F0DAD44D5EB8233E5D83F4333A8A3FCCADC77095F50F361F06D65E0736B",
            18_484_678)
        : new(
            "vulkan",
            "https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.vulkan/1.9.1/whisper.net.runtime.vulkan.1.9.1.nupkg",
            "25FBEA771D8AAFFC4588A1D6AA1345A7BBC74912A8ADA895D1695BBACFC1135A",
            36_870_982);

    private sealed record RuntimePackage(string Key, string Url, string Sha256, long DownloadBytes);
}

public sealed record RuntimeDownloadProgress(long DownloadedBytes, long TotalBytes)
{
    public int Percent => TotalBytes <= 0
        ? 0
        : (int)Math.Clamp(DownloadedBytes * 100 / TotalBytes, 0, 100);
}

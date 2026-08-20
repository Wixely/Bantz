using Bantz.Settings;
using Bantz.Transcription;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class WhisperRuntimeManagerTests : IDisposable
{
    private static readonly string[] CpuFiles = WhisperRuntimeManager.RequiredFileNames(TranscriptionRuntime.Cpu);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bantz-runtime-tests-{Guid.NewGuid():N}");

    [Fact]
    public void RuntimeIsAvailableOnlyWhenEveryNativeFileExists()
    {
        var manager = new WhisperRuntimeManager(_root);
        Assert.False(manager.IsInstalled(TranscriptionRuntime.Cpu));

        var directory = Path.Combine(_root, "whisper.net-1.9.1", "runtimes", WhisperRuntimeManager.PlatformDirectory);
        Directory.CreateDirectory(directory);
        foreach (var file in CpuFiles)
        {
            File.WriteAllText(Path.Combine(directory, file), "test");
        }

        Assert.True(manager.IsInstalled(TranscriptionRuntime.Cpu));
        Assert.False(manager.IsInstalled(TranscriptionRuntime.Automatic));
    }

    [Fact]
    public void GpuPackageIsLargerThanCpuPackage()
    {
        Assert.True(
            WhisperRuntimeManager.DownloadBytes(TranscriptionRuntime.Automatic) >
            WhisperRuntimeManager.DownloadBytes(TranscriptionRuntime.Cpu));
    }

    public void Dispose()
    {
        foreach (var file in CpuFiles)
        {
            var path = Path.Combine(_root, "whisper.net-1.9.1", "runtimes", WhisperRuntimeManager.PlatformDirectory, file);
            if (File.Exists(path)) File.Delete(path);
        }

        var version = Path.Combine(_root, "whisper.net-1.9.1");
        var architecture = Path.Combine(version, "runtimes", WhisperRuntimeManager.PlatformDirectory);
        var runtimes = Path.Combine(version, "runtimes");
        if (Directory.Exists(architecture)) Directory.Delete(architecture);
        if (Directory.Exists(runtimes)) Directory.Delete(runtimes);
        if (Directory.Exists(version)) Directory.Delete(version);
        if (Directory.Exists(_root)) Directory.Delete(_root);
    }
}

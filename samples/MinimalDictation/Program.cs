using Bantz.Input;
using Bantz.Speech;

Console.WriteLine($"Global bindings supported: {GlobalInputCapabilities.Current.SupportsGlobalBindings}");
ITranscriptionEngine engine = new ExampleRemoteEngine();
var result = await engine.TranscribeAsync(new PcmAudio(new byte[320]));
Console.WriteLine(result.Text);

file sealed class ExampleRemoteEngine : ITranscriptionEngine
{
    public Task<TranscriptionResult> TranscribeAsync(
        PcmAudio audio,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranscriptionResult($"Received {audio.Duration.TotalMilliseconds:N0} ms of PCM"));
}

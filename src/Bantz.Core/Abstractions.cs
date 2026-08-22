namespace Bantz.Core;

public interface ITextInjector
{
    TextInjectionResult InjectIntoForeground(string text, bool pressEnter);
}

public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class SystemAsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, cancellationToken);
}

public readonly record struct TextInjectionResult(bool Succeeded, string? Error = null)
{
    public static TextInjectionResult Success() => new(true);
    public static TextInjectionResult Failure(string error) => new(false, error);
}

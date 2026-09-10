namespace Bantz.Capture;

/// <summary>
/// Where a recording session puts the audio it has already emitted as frames: all of it, or none
/// of it.
///
/// <para>Hold-to-talk wants all of it — the press lasts seconds and the buffer is the product. An
/// always-open microphone wants none of it: at 16 kHz mono s16 a room microphone left open accrues
/// about 115 MB an hour that nothing will ever read.</para>
/// </summary>
internal sealed class CaptureBuffer : IDisposable
{
    private readonly MemoryStream? _retained;

    public CaptureBuffer(bool retain) => _retained = retain ? new MemoryStream() : null;

    /// <summary>Whether stopping will return the audio, rather than an empty buffer.</summary>
    public bool Retains => _retained is not null;

    /// <summary>
    /// The stream to copy capture output into. <see cref="Stream.Null"/> while streaming, so a
    /// producer that writes as it reads costs nothing and needs no branch of its own.
    /// </summary>
    public Stream Destination => _retained ?? Stream.Null;

    public void Write(ReadOnlySpan<byte> frame) => _retained?.Write(frame);

    /// <summary>Everything captured, or an empty array while streaming.</summary>
    public byte[] ToArray() => _retained?.ToArray() ?? [];

    public void Dispose() => _retained?.Dispose();
}

using System.Net;

namespace TTSMixmaster.Core.Tests.Services;

/// <summary>
/// A 200 response whose headers arrive at once but whose body sends a few bytes and then stalls until the
/// request is cancelled: HttpClient.Timeout doesn't cover body reads after ResponseHeadersRead.
/// </summary>
internal sealed class StalledStream(byte[] prefix) : Stream
{
    private int _position;

    public static HttpResponseMessage Response(byte[]? prefix = null) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(prefix ?? [0x89, 0x50])) };

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position < prefix.Length)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
        await Task.Delay(Timeout.Infinite, ct);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Reads are async only.");
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

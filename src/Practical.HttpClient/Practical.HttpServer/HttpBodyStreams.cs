using System.Globalization;
using System.Text;

namespace Practical.HttpServer;

internal sealed class ContentLengthReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _contentLength;
    private long _remaining;

    public ContentLengthReadStream(Stream inner, long contentLength)
    {
        _inner = inner;
        _contentLength = contentLength;
        _remaining = contentLength;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _contentLength;
    public override long Position
    {
        get => _contentLength - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining == 0)
            return 0;

        int read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        EnsureCompleteRead(read);
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_remaining == 0)
            return 0;

        int read = await _inner.ReadAsync(
            buffer[..(int)Math.Min(buffer.Length, _remaining)],
            cancellationToken);
        EnsureCompleteRead(read);
        _remaining -= read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void EnsureCompleteRead(int read)
    {
        if (read == 0 && _remaining > 0)
            throw new EndOfStreamException("The request body ended before Content-Length bytes were received.");
    }
}

internal sealed class ChunkedWriteStream(Stream inner) : Stream
{
    private static readonly byte[] ChunkTerminator = "\r\n"u8.ToArray();
    private static readonly byte[] FinalChunk = "0\r\n\r\n"u8.ToArray();
    private bool _completed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (count == 0)
            return;

        byte[] header = Encoding.ASCII.GetBytes(count.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
        inner.Write(header);
        inner.Write(buffer, offset, count);
        inner.Write(ChunkTerminator);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (buffer.Length == 0)
            return;

        byte[] header = Encoding.ASCII.GetBytes(
            buffer.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
        await inner.WriteAsync(header, cancellationToken);
        await inner.WriteAsync(buffer, cancellationToken);
        await inner.WriteAsync(ChunkTerminator, cancellationToken);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
            return;

        _completed = true;
        await inner.WriteAsync(FinalChunk, cancellationToken);
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

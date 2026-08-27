using System.Text;

namespace Practical.HttpServer;

public sealed class SimpleHttpContent
{
    private readonly Stream _body;

    internal SimpleHttpContent(Stream body, SimpleHttpHeaders headers)
    {
        _body = body;
        Headers = headers;
    }

    public SimpleHttpHeaders Headers { get; }

    public long? Length => Headers.ContentLength ?? (_body.CanSeek ? _body.Length : null);

    public async Task<string> ReadAsStringAsync(
        Encoding? encoding = null,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(
            _body,
            encoding ?? Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task<byte[]> ReadAsByteArrayAsync(CancellationToken cancellationToken = default)
    {
        using var destination = new MemoryStream();
        await CopyToAsync(destination, cancellationToken);
        return destination.ToArray();
    }

    public Stream ReadAsStream() => _body;

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await _body.CopyToAsync(destination, cancellationToken);
    }
}

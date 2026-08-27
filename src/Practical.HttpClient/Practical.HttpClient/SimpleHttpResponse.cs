using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Practical.HttpClient;

public sealed class SimpleHttpResponse : IDisposable, IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private int _contentConsumed;
    private bool _disposed;

    internal SimpleHttpResponse(
        TcpClient client,
        Stream stream,
        Uri requestUri,
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers)
    {
        _client = client;
        _stream = stream;
        RequestUri = requestUri;
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Headers = headers;
    }

    public int StatusCode { get; }

    public Uri RequestUri { get; }

    public string ReasonPhrase { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;

    public async Task<string> ReadAsStringAsync(
        Encoding? encoding = null,
        CancellationToken cancellationToken = default)
    {
        byte[] content = await ReadAsByteArrayAsync(cancellationToken);
        return (encoding ?? Encoding.UTF8).GetString(content);
    }

    public async Task<byte[]> ReadAsByteArrayAsync(CancellationToken cancellationToken = default)
    {
        using var content = new MemoryStream();
        await CopyContentToAsync(content, cancellationToken);
        return content.ToArray();
    }

    public async Task SaveContentToFileAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string temporaryPath = $"{fullDestinationPath}.{Guid.NewGuid():N}.download";

        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await CopyContentToAsync(destination, cancellationToken);
            }

            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }

    public async Task CopyContentToAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _contentConsumed, 1) != 0)
            throw new InvalidOperationException("The response content has already been consumed.");

        if (Headers.TryGetValue("Transfer-Encoding", out string? transferEncoding) &&
            transferEncoding.Split(',').Any(value =>
                value.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase)))
        {
            await CopyChunkedContentAsync(destination, cancellationToken);
            return;
        }

        if (Headers.TryGetValue("Content-Length", out string? contentLengthValue))
        {
            if (!long.TryParse(
                    contentLengthValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long contentLength) || contentLength < 0)
            {
                throw new HttpRequestException($"Invalid Content-Length header: {contentLengthValue}");
            }

            await CopyExactlyAsync(destination, contentLength, cancellationToken);
            return;
        }

        await _stream.CopyToAsync(destination, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stream.Dispose();
        _client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _stream.DisposeAsync();
        _client.Dispose();
    }

    private async Task CopyChunkedContentAsync(
        Stream destination,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            string chunkHeader = await ReadAsciiLineAsync(cancellationToken);
            string chunkSizeText = chunkHeader.Split(';', 2)[0].Trim();
            if (!long.TryParse(
                    chunkSizeText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out long chunkSize) || chunkSize < 0)
            {
                throw new HttpRequestException($"Invalid chunk size: {chunkHeader}");
            }

            if (chunkSize == 0)
            {
                while ((await ReadAsciiLineAsync(cancellationToken)).Length != 0)
                {
                    // Consume optional trailer headers.
                }
                return;
            }

            await CopyExactlyAsync(destination, chunkSize, cancellationToken);
            if (await ReadByteAsync(cancellationToken) != '\r' ||
                await ReadByteAsync(cancellationToken) != '\n')
            {
                throw new HttpRequestException("Invalid chunk terminator.");
            }
        }
    }

    private async Task CopyExactlyAsync(
        Stream destination,
        long byteCount,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        while (byteCount > 0)
        {
            int read = await _stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, byteCount)),
                cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("The HTTP response ended before all content was received.");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            byteCount -= read;
        }
    }

    private async Task<string> ReadAsciiLineAsync(CancellationToken cancellationToken)
    {
        const int maximumLineLength = 64 * 1024;
        var line = new List<byte>();

        while (line.Count < maximumLineLength)
        {
            int current = await ReadByteAsync(cancellationToken);
            if (current < 0)
                throw new EndOfStreamException("The HTTP response ended while reading chunk metadata.");

            if (current == '\n')
            {
                if (line.Count > 0 && line[^1] == '\r')
                    line.RemoveAt(line.Count - 1);
                return Encoding.ASCII.GetString(line.ToArray());
            }

            line.Add((byte)current);
        }

        throw new HttpRequestException("A chunk metadata line exceeded 64 KB.");
    }

    private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        int read = await _stream.ReadAsync(buffer, cancellationToken);
        return read == 0 ? -1 : buffer[0];
    }
}

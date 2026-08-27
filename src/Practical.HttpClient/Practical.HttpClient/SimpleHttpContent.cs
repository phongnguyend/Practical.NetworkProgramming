using System.Globalization;
using System.Text;

namespace Practical.HttpClient;

public sealed class SimpleHttpContent
{
    private readonly Stream _stream;
    private int _consumed;
    private bool _disposed;

    internal SimpleHttpContent(
        Stream stream,
        SimpleHttpHeaders headers)
    {
        _stream = stream;
        Headers = headers;
    }

    public SimpleHttpHeaders Headers { get; }

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
        await CopyToAsync(content, cancellationToken);
        return content.ToArray();
    }

    public async Task SaveToFileAsync(
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
                await CopyToAsync(destination, cancellationToken);
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

    public async Task CopyToAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new InvalidOperationException("The response content has already been consumed.");

        if (Headers.TransferEncoding.Any(value =>
                value.Equals("chunked", StringComparison.OrdinalIgnoreCase)))
        {
            await CopyChunkedContentAsync(destination, cancellationToken);
            return;
        }

        if (Headers.ContainsKey("Content-Length"))
        {
            if (Headers.ContentLength is not long contentLength)
                throw new HttpRequestException($"Invalid Content-Length header: {Headers["Content-Length"]}");

            await CopyExactlyAsync(destination, contentLength, cancellationToken);
            return;
        }

        await _stream.CopyToAsync(destination, cancellationToken);
    }

    internal void MarkDisposed() => _disposed = true;

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

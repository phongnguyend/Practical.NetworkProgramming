using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Practical.HttpClient;

public class SimpleHttpClient
{
    private readonly SimpleHttpClientOptions _options;

    public SimpleHttpClient(SimpleHttpClientOptions? options = null)
    {
        _options = options ?? new SimpleHttpClientOptions();
    }

    public Task<SimpleHttpResponse> GetAsync(
        string url,
        CancellationToken cancellationToken = default)
        => SendAsync("GET", url, null, cancellationToken);

    public Task<SimpleHttpResponse> PostAsync(
        string url,
        HttpContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return SendAsync("POST", url, content, cancellationToken);
    }

    public Task<SimpleHttpResponse> PutAsync(
        string url,
        HttpContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return SendAsync("PUT", url, content, cancellationToken);
    }

    public Task<SimpleHttpResponse> DeleteAsync(
        string url,
        CancellationToken cancellationToken = default)
        => SendAsync("DELETE", url, null, cancellationToken);

    private async Task<SimpleHttpResponse> SendAsync(
        string method,
        string url,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var uri = new Uri(url, UriKind.Absolute);
        Stream? contentStream = content is null
            ? null
            : await content.ReadAsStreamAsync(cancellationToken);
        long initialPosition = contentStream?.CanSeek == true ? contentStream.Position : 0;
        bool sendContent = content is not null;
        bool contentSent = false;

        for (int redirectCount = 0; ; redirectCount++)
        {
            if (sendContent && contentSent)
            {
                if (contentStream?.CanSeek != true)
                {
                    throw new HttpRequestException(
                        "The request content cannot be replayed for a 307 or 308 redirect.");
                }

                contentStream.Position = initialPosition;
            }

            SimpleHttpResponse response = await SendOnceAsync(
                method,
                uri,
                sendContent ? content : null,
                sendContent ? contentStream : null,
                cancellationToken);
            contentSent |= sendContent;

            if (!_options.AllowAutoRedirect ||
                !IsRedirect(response.StatusCode) ||
                !response.Headers.TryGetValue("Location", out string? location))
            {
                return response;
            }

            await response.DisposeAsync();

            if (redirectCount >= _options.MaxAutomaticRedirections)
            {
                throw new HttpRequestException(
                    $"The request exceeded the maximum of {_options.MaxAutomaticRedirections} redirects.");
            }

            try
            {
                uri = new Uri(uri, location);
            }
            catch (UriFormatException exception)
            {
                throw new HttpRequestException($"The server returned an invalid redirect location: {location}", exception);
            }

            if (response.StatusCode == 303 ||
                (response.StatusCode is 301 or 302 && method.Equals("POST", StringComparison.OrdinalIgnoreCase)))
            {
                method = "GET";
                sendContent = false;
            }
        }
    }

    private static async Task<SimpleHttpResponse> SendOnceAsync(
        string method,
        Uri uri,
        HttpContent? content,
        Stream? contentStream,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new HttpRequestException($"Redirecting to the '{uri.Scheme}' URI scheme is not supported.");

        var client = new TcpClient();
        Stream? stream = null;

        try
        {
            await client.ConnectAsync(uri.Host, uri.Port, cancellationToken);
            stream = await GetStreamAsync(client, uri.Host, uri.Scheme == Uri.UriSchemeHttps);
            await WriteRequestAsync(stream, method, uri, content, contentStream, cancellationToken);

            var (statusCode, reasonPhrase, headers) =
                await ReadResponseHeadersAsync(stream, cancellationToken);

            return new SimpleHttpResponse(client, stream, uri, statusCode, reasonPhrase, headers);
        }
        catch
        {
            if (stream is not null)
                await stream.DisposeAsync();
            client.Dispose();
            throw;
        }
    }

    private static bool IsRedirect(int statusCode) => statusCode is 301 or 302 or 303 or 307 or 308;

    private static async Task WriteRequestAsync(
        Stream stream,
        string method,
        Uri uri,
        HttpContent? content,
        Stream? contentStream,
        CancellationToken cancellationToken)
    {
        string host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        long? contentLength = content?.Headers.ContentLength;
        if (contentLength is null && contentStream?.CanSeek == true)
            contentLength = contentStream.Length - contentStream.Position;

        var request = new StringBuilder();
        request.Append($"{method} {uri.PathAndQuery} HTTP/1.1\r\n");
        request.Append($"Host: {host}\r\n");
        if (content is not null)
        {
            foreach (var header in content.Headers)
            {
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    request.Append($"{header.Key}: {string.Join(", ", header.Value)}\r\n");
            }
        }

        if (content is not null)
        {
            if (contentLength is not null)
                request.Append($"Content-Length: {contentLength.Value}\r\n");
            else
                request.Append("Transfer-Encoding: chunked\r\n");
        }
        request.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancellationToken);
        if (contentStream is null)
            return;

        if (contentLength is not null)
            await contentStream.CopyToAsync(stream, cancellationToken);
        else
            await CopyAsChunkedAsync(contentStream, stream, cancellationToken);
    }

    private static async Task CopyAsChunkedAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            await destination.WriteAsync(
                Encoding.ASCII.GetBytes($"{read:X}\r\n"),
                cancellationToken);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await destination.WriteAsync("\r\n"u8.ToArray(), cancellationToken);
        }

        await destination.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken);
    }

    private static async Task<(int StatusCode, string ReasonPhrase, Dictionary<string, string> Headers)>
        ReadResponseHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        string statusLine = await ReadAsciiLineAsync(stream, cancellationToken);
        string[] statusParts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out int statusCode))
            throw new HttpRequestException($"Invalid HTTP status line: {statusLine}");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string line = await ReadAsciiLineAsync(stream, cancellationToken);
            if (line.Length == 0)
                break;

            int separator = line.IndexOf(':');
            if (separator <= 0)
                throw new HttpRequestException($"Invalid HTTP header: {line}");

            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out string? existing)
                ? $"{existing}, {value}"
                : value;
        }

        string reasonPhrase = statusParts.Length == 3 ? statusParts[2] : string.Empty;
        return (statusCode, reasonPhrase, headers);
    }

    private static async Task<string> ReadAsciiLineAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        const int maximumLineLength = 64 * 1024;
        var line = new List<byte>();
        byte[] buffer = new byte[1];

        while (line.Count < maximumLineLength)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("The HTTP response ended while reading headers.");

            if (buffer[0] == '\n')
            {
                if (line.Count > 0 && line[^1] == '\r')
                    line.RemoveAt(line.Count - 1);

                return Encoding.ASCII.GetString(line.ToArray());
            }

            line.Add(buffer[0]);
        }

        throw new HttpRequestException("An HTTP header line exceeded 64 KB.");
    }

    private static async Task<Stream> GetStreamAsync(TcpClient client, string host, bool https)
    {
        if (https)
        {
            var sslStream = new SslStream(client.GetStream(), false, (sender, cert, chain, errors) => true);
            await sslStream.AuthenticateAsClientAsync(host);
            return sslStream;
        }

        return client.GetStream();
    }
}

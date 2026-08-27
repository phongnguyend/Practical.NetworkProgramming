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
        => SendAsync("GET", url, cancellationToken: cancellationToken);

    public Task<SimpleHttpResponse> PostAsync(
        string url,
        string? body = null,
        string contentType = "application/json",
        CancellationToken cancellationToken = default)
        => SendAsync("POST", url, body, contentType, cancellationToken);

    public Task<SimpleHttpResponse> PutAsync(
        string url,
        string? body = null,
        string contentType = "application/json",
        CancellationToken cancellationToken = default)
        => SendAsync("PUT", url, body, contentType, cancellationToken);

    public Task<SimpleHttpResponse> DeleteAsync(
        string url,
        CancellationToken cancellationToken = default)
        => SendAsync("DELETE", url, cancellationToken: cancellationToken);

    private async Task<SimpleHttpResponse> SendAsync(
        string method,
        string url,
        string? body = null,
        string contentType = "application/json",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var uri = new Uri(url, UriKind.Absolute);
        for (int redirectCount = 0; ; redirectCount++)
        {
            SimpleHttpResponse response = await SendOnceAsync(
                method,
                uri,
                body,
                contentType,
                cancellationToken);

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
                body = null;
            }
        }
    }

    private static async Task<SimpleHttpResponse> SendOnceAsync(
        string method,
        Uri uri,
        string? body,
        string contentType,
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
            await WriteRequestAsync(stream, method, uri, body, contentType, cancellationToken);

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
        string? body,
        string contentType,
        CancellationToken cancellationToken)
    {
        byte[]? bodyBytes = body is null ? null : Encoding.UTF8.GetBytes(body);
        string host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        var request = new StringBuilder();
        request.Append($"{method} {uri.PathAndQuery} HTTP/1.1\r\n");
        request.Append($"Host: {host}\r\n");
        if (bodyBytes is not null)
        {
            request.Append($"Content-Type: {contentType}\r\n");
            request.Append($"Content-Length: {bodyBytes.Length}\r\n");
        }
        request.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancellationToken);
        if (bodyBytes is not null)
            await stream.WriteAsync(bodyBytes, cancellationToken);
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

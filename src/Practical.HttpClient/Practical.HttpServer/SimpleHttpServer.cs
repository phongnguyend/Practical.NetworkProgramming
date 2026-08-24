using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Practical.HttpServer;

public sealed class SimpleHttpServer(IPAddress address, int port)
{
    private const int MaximumHeaderSize = 64 * 1024;
    private const int MaximumBodySize = 10 * 1024 * 1024;
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.Ordinal) { "GET", "POST", "PUT", "DELETE" };

    private readonly TcpListener _listener = new(address, port);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _listener.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _ = HandleClientSafelyAsync(client, cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
        }
    }

    private static async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Request failed: {exception.Message}");
            }
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using NetworkStream stream = client.GetStream();
        string? rawHeaders;

        try
        {
            rawHeaders = await ReadHeadersAsync(stream, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            await WriteErrorAsync(stream, 431, "Request Header Fields Too Large", exception.Message, cancellationToken);
            return;
        }

        if (rawHeaders is null)
            return;

        if (!TryParseRequest(rawHeaders, out var request, out string error))
        {
            await WriteErrorAsync(stream, 400, "Bad Request", error, cancellationToken);
            return;
        }

        if (!AllowedMethods.Contains(request.Method))
        {
            await WriteErrorAsync(
                stream,
                405,
                "Method Not Allowed",
                "Only GET, POST, PUT, and DELETE are supported.",
                cancellationToken,
                "Allow: GET, POST, PUT, DELETE\r\n");
            return;
        }

        if (!TryGetContentLength(request.Headers, out int contentLength, out error))
        {
            await WriteErrorAsync(stream, 400, "Bad Request", error, cancellationToken);
            return;
        }

        if (contentLength > MaximumBodySize)
        {
            await WriteErrorAsync(
                stream,
                413,
                "Content Too Large",
                $"The request body cannot exceed {MaximumBodySize} bytes.",
                cancellationToken);
            return;
        }

        if (contentLength > 0)
        {
            try
            {
                await DrainBodyAsync(stream, contentLength, cancellationToken);
            }
            catch (EndOfStreamException exception)
            {
                await WriteErrorAsync(stream, 400, "Bad Request", exception.Message, cancellationToken);
                return;
            }
        }

        var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        var response = new ClientInformation(
            request.Method,
            request.Headers.GetValueOrDefault("Host") ?? string.Empty,
            remoteEndPoint?.Address.ToString() ?? string.Empty,
            request.Path,
            request.QueryString,
            request.Headers);

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        await WriteResponseAsync(stream, 200, "OK", body, cancellationToken);
    }

    private static async Task<string?> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var singleByte = new byte[1];
        var terminator = new byte[] { 13, 10, 13, 10 };
        var matched = 0;

        while (bytes.Count < MaximumHeaderSize)
        {
            int read = await stream.ReadAsync(singleByte, cancellationToken);
            if (read == 0)
                return bytes.Count == 0 ? null : throw new InvalidDataException("The request headers are incomplete.");

            byte value = singleByte[0];
            bytes.Add(value);

            if (value == terminator[matched])
            {
                matched++;
                if (matched == terminator.Length)
                    return Encoding.ASCII.GetString(bytes.ToArray());
            }
            else
            {
                matched = value == terminator[0] ? 1 : 0;
            }
        }

        throw new InvalidDataException($"Request headers cannot exceed {MaximumHeaderSize} bytes.");
    }

    private static bool TryParseRequest(string rawHeaders, out ParsedRequest request, out string error)
    {
        request = default!;
        error = string.Empty;

        string[] lines = rawHeaders.Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            error = "The HTTP request line is invalid.";
            return false;
        }

        string method = requestLine[0].ToUpperInvariant();
        string target = requestLine[1];
        int queryStart = target.IndexOf('?');
        string path = queryStart >= 0 ? target[..queryStart] : target;
        string queryString = queryStart >= 0 ? target[queryStart..] : string.Empty;

        if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
        {
            path = absoluteUri.AbsolutePath;
            queryString = absoluteUri.Query;
        }
        else if (!target.StartsWith('/'))
        {
            error = "The request target must be an absolute path.";
            return false;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
        {
            int separator = lines[i].IndexOf(':');
            if (separator <= 0)
            {
                error = $"Invalid header: {lines[i]}";
                return false;
            }

            string name = lines[i][..separator].Trim();
            string value = lines[i][(separator + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out string? existing)
                ? $"{existing}, {value}"
                : value;
        }

        request = new ParsedRequest(method, path, queryString, headers);
        return true;
    }

    private static bool TryGetContentLength(
        IReadOnlyDictionary<string, string> headers,
        out int contentLength,
        out string error)
    {
        contentLength = 0;
        error = string.Empty;

        if (headers.ContainsKey("Transfer-Encoding"))
        {
            error = "Transfer-Encoding is not supported; send a Content-Length header instead.";
            return false;
        }

        if (!headers.TryGetValue("Content-Length", out string? value))
            return true;

        if (!int.TryParse(value, out contentLength) || contentLength < 0)
        {
            error = "Content-Length must be a non-negative integer.";
            return false;
        }

        return true;
    }

    private static async Task DrainBodyAsync(
        NetworkStream stream,
        int contentLength,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(contentLength, 8192)];
        int remaining = contentLength;

        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("The request body ended before Content-Length bytes were received.");

            remaining -= read;
        }
    }

    private static Task WriteErrorAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string message,
        CancellationToken cancellationToken,
        string additionalHeaders = "")
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { error = message }, JsonOptions);
        return WriteResponseAsync(stream, statusCode, reasonPhrase, body, cancellationToken, additionalHeaders);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        byte[] body,
        CancellationToken cancellationToken,
        string additionalHeaders = "")
    {
        string headers =
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            additionalHeaders +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed record ParsedRequest(
        string Method,
        string Path,
        string QueryString,
        Dictionary<string, string> Headers);

    private sealed record ClientInformation(
        string Method,
        string Host,
        string Ip,
        string Path,
        string QueryString,
        IReadOnlyDictionary<string, string> Headers);
}

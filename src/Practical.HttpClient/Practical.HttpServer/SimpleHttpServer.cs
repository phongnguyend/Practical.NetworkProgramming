using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Practical.HttpServer;

public sealed class SimpleHttpServer(IPAddress address, int port)
{
    private const int MaximumHeaderSize = 64 * 1024;
    private const int MaximumBodySize = 10 * 1024 * 1024;
    private const int MaximumWebSocketMessageSize = 1024 * 1024;
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.Ordinal) { "GET", "POST", "PUT", "DELETE" };

    private readonly TcpListener _listener = new(address, port);

    public Func<SimpleHttpContext, CancellationToken, Task>? RequestHandler { get; set; }

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

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken cancellationToken)
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

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
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

        if (request.Headers.IsWebSocketAttempt())
        {
            await HandleWebSocketUpgradeAsync(stream, request, cancellationToken);
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

        if (!request.Headers.TryGetContentLength(out int contentLength, out error))
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

        Stream requestBody = contentLength == 0
            ? Stream.Null
            : new ContentLengthReadStream(stream, contentLength);
        request.SetContent(requestBody);

        var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        var clientInformation = new ClientInformation(
            request.Method,
            request.Headers.Host ?? string.Empty,
            remoteEndPoint?.Address.ToString() ?? string.Empty,
            request.Path,
            request.QueryString,
            request.Headers,
            request.Content.Length ?? 0);
        var context = new SimpleHttpContext(request, new SimpleHttpResponse(stream));

        if (request.Headers.ContainsToken("Expect", "100-continue") && contentLength > 0)
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken);

        if (RequestHandler is not null)
            await RequestHandler(context, cancellationToken);

        try
        {
            await request.Content.CopyToAsync(Stream.Null, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            if (context.Response.HasStarted)
                throw;

            await WriteErrorAsync(stream, 400, "Bad Request", exception.Message, cancellationToken);
            return;
        }

        if (RequestHandler is null)
            await context.Response.WriteJsonAsync(clientInformation, cancellationToken);

        await context.Response.CompleteAsync(cancellationToken);
    }

    private static async Task HandleWebSocketUpgradeAsync(
        NetworkStream stream,
        SimpleHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Path != "/ws")
        {
            await WriteErrorAsync(stream, 404, "Not Found", "The WebSocket endpoint is /ws.", cancellationToken);
            return;
        }

        if (request.Method != "GET" || request.Version != "HTTP/1.1")
        {
            await WriteErrorAsync(
                stream,
                400,
                "Bad Request",
                "A WebSocket upgrade requires GET and HTTP/1.1.",
                cancellationToken);
            return;
        }

        if (!request.Headers.ContainsToken("Connection", "Upgrade") ||
            !request.Headers.ContainsToken("Upgrade", "websocket"))
        {
            await WriteErrorAsync(
                stream,
                400,
                "Bad Request",
                "Connection: Upgrade and Upgrade: websocket headers are required.",
                cancellationToken);
            return;
        }

        if (request.Headers.WebSocketVersion != "13")
        {
            await WriteErrorAsync(
                stream,
                426,
                "Upgrade Required",
                "Only WebSocket version 13 is supported.",
                cancellationToken,
                "Sec-WebSocket-Version: 13\r\n");
            return;
        }

        if (request.Headers.WebSocketKey is not string key || !IsValidWebSocketKey(key))
        {
            await WriteErrorAsync(
                stream,
                400,
                "Bad Request",
                "Sec-WebSocket-Key must be a Base64-encoded 16-byte value.",
                cancellationToken);
            return;
        }

        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);

        using WebSocket webSocket = WebSocket.CreateFromStream(
            stream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(30));

        await RunWebSocketEchoLoopAsync(webSocket, cancellationToken);
    }

    private static bool IsValidWebSocketKey(string key)
    {
        try
        {
            return Convert.FromBase64String(key.Trim()).Length == 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task RunWebSocketEchoLoopAsync(
        WebSocket webSocket,
        CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[8192];

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await webSocket.ReceiveAsync(receiveBuffer, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseOutputAsync(
                            result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result.CloseStatusDescription,
                            cancellationToken);
                        return;
                    }

                    if (message.Length + result.Count > MaximumWebSocketMessageSize)
                    {
                        await webSocket.CloseOutputAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            $"Messages cannot exceed {MaximumWebSocketMessageSize} bytes.",
                            cancellationToken);
                        return;
                    }

                    await message.WriteAsync(receiveBuffer.AsMemory(0, result.Count), cancellationToken);
                }
                while (!result.EndOfMessage);

                await webSocket.SendAsync(
                    message.GetBuffer().AsMemory(0, checked((int)message.Length)),
                    result.MessageType,
                    endOfMessage: true,
                    cancellationToken);
            }
        }
        catch (WebSocketException)
        {
            // The peer disconnected without completing the close handshake.
        }
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

    private static bool TryParseRequest(string rawHeaders, out SimpleHttpRequest request, out string error)
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

        request = new SimpleHttpRequest(
            method,
            requestLine[2],
            path,
            queryString,
            new SimpleHttpHeaders(headers));
        return true;
    }

    private static async Task WriteErrorAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string message,
        CancellationToken cancellationToken,
        string additionalHeaders = "")
    {
        var response = new SimpleHttpResponse(stream)
        {
            StatusCode = statusCode,
            ReasonPhrase = reasonPhrase
        };

        foreach (string header in additionalHeaders.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = header.IndexOf(':');
            if (separator > 0)
                response.Headers[header[..separator].Trim()] = header[(separator + 1)..].Trim();
        }

        await response.WriteJsonAsync(new { error = message }, cancellationToken);
        await response.CompleteAsync(cancellationToken);
    }

    private sealed record ClientInformation(
        string Method,
        string Host,
        string Ip,
        string Path,
        string QueryString,
        IReadOnlyDictionary<string, string> Headers,
        long BodyLength);
}

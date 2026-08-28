using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Practical.HttpServer;

public sealed class SimpleHttpServer
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private readonly SimpleHttpServerOptions _options;
    private readonly TcpListener _listener;
    private readonly Dictionary<string, Func<SimpleWebSocketContext, CancellationToken, Task>>
        _webSocketHandlers = new(StringComparer.Ordinal);
    private readonly object _webSocketHandlersLock = new();

    public SimpleHttpServer(
        IPAddress address,
        int port,
        SimpleHttpServerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        _options = options ?? new SimpleHttpServerOptions();
        ArgumentNullException.ThrowIfNull(_options.AllowedMethods);
        if (_options.AllowedMethods.Count == 0 || _options.AllowedMethods.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one valid HTTP method must be allowed.", nameof(options));
        _listener = new TcpListener(address, port);
    }

    public required Func<SimpleHttpContext, CancellationToken, Task> RequestHandler { get; set; }

    public SimpleHttpServer MapWebSocket(
        string path,
        Func<SimpleWebSocketContext, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(handler);
        if (!path.StartsWith('/') || path.Contains('?') || path.Contains('#'))
        {
            throw new ArgumentException(
                "A WebSocket path must start with '/' and cannot contain a query string or fragment.",
                nameof(path));
        }

        lock (_webSocketHandlersLock)
        {
            if (!_webSocketHandlers.TryAdd(path, handler))
                throw new InvalidOperationException($"A WebSocket handler is already mapped to '{path}'.");
        }

        return this;
    }

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
        var response = new SimpleHttpResponse(stream);
        string? rawHeaders;

        try
        {
            rawHeaders = await ReadHeadersAsync(stream, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            await response.WriteErrorAsync(431, "Request Header Fields Too Large", exception.Message, cancellationToken);
            return;
        }

        if (rawHeaders is null)
            return;

        if (!TryParseRequest(rawHeaders, out var request, out string error))
        {
            await response.WriteErrorAsync(400, "Bad Request", error, cancellationToken);
            return;
        }

        if (request.Headers.IsWebSocketAttempt())
        {
            if (!TryGetWebSocketHandler(request.Path, out var webSocketHandler))
            {
                await response.WriteErrorAsync(
                    404,
                    "Not Found",
                    $"No WebSocket handler is mapped to '{request.Path}'.",
                    cancellationToken);
                return;
            }

            await HandleWebSocketUpgradeAsync(
                client.Client,
                stream,
                response,
                request,
                webSocketHandler,
                cancellationToken);
            return;
        }

        if (!_options.AllowedMethods.Contains(request.Method, StringComparer.OrdinalIgnoreCase))
        {
            string allowedMethods = string.Join(", ", _options.AllowedMethods);
            await response.WriteErrorAsync(
                405,
                "Method Not Allowed",
                $"Only {allowedMethods} are supported.",
                cancellationToken,
                $"Allow: {allowedMethods}\r\n");
            return;
        }

        if (!request.Headers.TryGetContentLength(out int contentLength, out error))
        {
            await response.WriteErrorAsync(400, "Bad Request", error, cancellationToken);
            return;
        }

        if (contentLength > _options.MaximumRequestBodySize)
        {
            await response.WriteErrorAsync(
                413,
                "Content Too Large",
                $"The request body cannot exceed {_options.MaximumRequestBodySize} bytes.",
                cancellationToken);
            return;
        }

        Stream requestBody = contentLength == 0
            ? Stream.Null
            : new ContentLengthReadStream(stream, contentLength);
        request.SetContent(requestBody);

        var context = new SimpleHttpContext(
            client.Client,
            request,
            response);

        if (request.Headers.ContainsToken("Expect", "100-continue") && contentLength > 0)
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken);

        await RequestHandler(context, cancellationToken);

        try
        {
            await request.Content.DrainAsync(cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            if (context.Response.HasStarted)
                throw;

            await response.WriteErrorAsync(400, "Bad Request", exception.Message, cancellationToken);
            return;
        }

        await context.Response.CompleteAsync(cancellationToken);
    }

    private async Task HandleWebSocketUpgradeAsync(
        Socket socket,
        NetworkStream stream,
        SimpleHttpResponse response,
        SimpleHttpRequest request,
        Func<SimpleWebSocketContext, CancellationToken, Task> webSocketHandler,
        CancellationToken cancellationToken)
    {
        if (request.Method != "GET" || request.Version != "HTTP/1.1")
        {
            await response.WriteErrorAsync(
                400,
                "Bad Request",
                "A WebSocket upgrade requires GET and HTTP/1.1.",
                cancellationToken);
            return;
        }

        if (!request.Headers.ContainsToken("Connection", "Upgrade") ||
            !request.Headers.ContainsToken("Upgrade", "websocket"))
        {
            await response.WriteErrorAsync(
                400,
                "Bad Request",
                "Connection: Upgrade and Upgrade: websocket headers are required.",
                cancellationToken);
            return;
        }

        if (request.Headers.WebSocketVersion != "13")
        {
            await response.WriteErrorAsync(
                426,
                "Upgrade Required",
                "Only WebSocket version 13 is supported.",
                cancellationToken,
                "Sec-WebSocket-Version: 13\r\n");
            return;
        }

        if (request.Headers.WebSocketKey is not string key || !IsValidWebSocketKey(key))
        {
            await response.WriteErrorAsync(
                400,
                "Bad Request",
                "Sec-WebSocket-Key must be a Base64-encoded 16-byte value.",
                cancellationToken);
            return;
        }

        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));
        string handshakeResponse =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshakeResponse), cancellationToken);

        using WebSocket webSocket = WebSocket.CreateFromStream(
            stream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: _options.WebSocketKeepAliveInterval);

        await RunWebSocketLoopAsync(
            socket,
            request,
            webSocket,
            webSocketHandler,
            cancellationToken);
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

    private async Task RunWebSocketLoopAsync(
        Socket socket,
        SimpleHttpRequest request,
        WebSocket webSocket,
        Func<SimpleWebSocketContext, CancellationToken, Task> webSocketHandler,
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

                    if (message.Length + result.Count > _options.MaximumWebSocketMessageSize)
                    {
                        await webSocket.CloseOutputAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            $"Messages cannot exceed {_options.MaximumWebSocketMessageSize} bytes.",
                            cancellationToken);
                        return;
                    }

                    await message.WriteAsync(receiveBuffer.AsMemory(0, result.Count), cancellationToken);
                }
                while (!result.EndOfMessage);

                var context = new SimpleWebSocketContext(
                    socket,
                    request,
                    webSocket,
                    message.GetBuffer().AsMemory(0, checked((int)message.Length)),
                    result.MessageType);
                await webSocketHandler(context, cancellationToken);
            }
        }
        catch (WebSocketException)
        {
            // The peer disconnected without completing the close handshake.
        }
    }

    private bool TryGetWebSocketHandler(
        string path,
        out Func<SimpleWebSocketContext, CancellationToken, Task> handler)
    {
        lock (_webSocketHandlersLock)
            return _webSocketHandlers.TryGetValue(path, out handler!);
    }

    private async Task<string?> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var singleByte = new byte[1];
        var terminator = new byte[] { 13, 10, 13, 10 };
        var matched = 0;

        while (bytes.Count < _options.MaximumRequestHeaderSize)
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

        throw new InvalidDataException(
            $"Request headers cannot exceed {_options.MaximumRequestHeaderSize} bytes.");
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

}

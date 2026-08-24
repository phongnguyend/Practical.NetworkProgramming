using System.Net.WebSockets;
using System.Text;

namespace Practical.WebSocketClient;

public sealed class SimpleWebSocketClient : IAsyncDisposable
{
    private const int MaximumMessageSize = 1024 * 1024;
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;

    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint.Scheme is not ("ws" or "wss"))
            throw new ArgumentException("The endpoint must use the ws or wss scheme.", nameof(endpoint));

        return _socket.ConnectAsync(endpoint, cancellationToken);
    }

    public async Task<string> EchoTextAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message);
        ReceivedMessage response = await SendAndReceiveAsync(
            payload,
            WebSocketMessageType.Text,
            cancellationToken);

        if (response.MessageType != WebSocketMessageType.Text)
            throw new InvalidDataException($"Expected a text response, but received {response.MessageType}.");

        return Encoding.UTF8.GetString(response.Payload);
    }

    public async Task<byte[]> EchoBinaryAsync(
        byte[] message,
        CancellationToken cancellationToken = default)
    {
        ReceivedMessage response = await SendAndReceiveAsync(
            message,
            WebSocketMessageType.Binary,
            cancellationToken);

        if (response.MessageType != WebSocketMessageType.Binary)
            throw new InvalidDataException($"Expected a binary response, but received {response.MessageType}.");

        return response.Payload;
    }

    public async Task<string> EchoFragmentedTextAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        byte[] payload = Encoding.UTF8.GetBytes(message);
        int splitAt = payload.Length / 2;

        await _socket.SendAsync(
            payload.AsMemory(0, splitAt),
            WebSocketMessageType.Text,
            endOfMessage: false,
            cancellationToken);
        await _socket.SendAsync(
            payload.AsMemory(splitAt),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);

        ReceivedMessage response = await ReceiveMessageAsync(cancellationToken);
        if (response.MessageType != WebSocketMessageType.Text)
            throw new InvalidDataException($"Expected a text response, but received {response.MessageType}.");

        return Encoding.UTF8.GetString(response.Payload);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Client closing",
                cancellationToken);
        }
        else if (_socket.State == WebSocketState.CloseReceived)
        {
            await _socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "Client acknowledging close",
                cancellationToken);
        }
    }

    private async Task<ReceivedMessage> SendAndReceiveAsync(
        byte[] payload,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        EnsureConnected();

        await _socket.SendAsync(payload, messageType, endOfMessage: true, cancellationToken);
        return await ReceiveMessageAsync(cancellationToken);
    }

    private async Task<ReceivedMessage> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _socket.CloseOutputAsync(
                    result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    result.CloseStatusDescription,
                    cancellationToken);
                throw new IOException("The server closed the WebSocket connection.");
            }

            if (message.Length + result.Count > MaximumMessageSize)
                throw new InvalidDataException($"The response exceeds {MaximumMessageSize} bytes.");

            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        }
        while (!result.EndOfMessage);

        return new ReceivedMessage(result.MessageType, message.ToArray());
    }

    private void EnsureConnected()
    {
        if (_socket.State != WebSocketState.Open)
            throw new InvalidOperationException($"The WebSocket is not open. Current state: {_socket.State}.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await CloseAsync(timeout.Token);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or WebSocketException or IOException)
        {
            _socket.Abort();
        }
        finally
        {
            _socket.Dispose();
        }
    }

    private sealed record ReceivedMessage(WebSocketMessageType MessageType, byte[] Payload);
}

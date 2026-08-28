using System.Net.Sockets;
using System.Net.WebSockets;

namespace Practical.HttpServer;

public sealed class SimpleWebSocketContext
{
    internal SimpleWebSocketContext(
        Socket socket,
        SimpleHttpRequest request,
        WebSocket webSocket,
        ReadOnlyMemory<byte> message,
        WebSocketMessageType messageType)
    {
        Socket = socket;
        Request = request;
        WebSocket = webSocket;
        Message = message;
        MessageType = messageType;
    }

    public Socket Socket { get; }

    public SimpleHttpRequest Request { get; }

    public string Path => Request.Path;

    public WebSocket WebSocket { get; }

    public ReadOnlyMemory<byte> Message { get; }

    public WebSocketMessageType MessageType { get; }
}

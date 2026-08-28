using System.Net.Sockets;

namespace Practical.HttpServer;

public sealed class SimpleHttpContext
{
    internal SimpleHttpContext(
        Socket socket,
        SimpleHttpRequest request,
        SimpleHttpResponse response)
    {
        Socket = socket;
        Request = request;
        Response = response;
    }

    public Socket Socket { get; }

    public SimpleHttpRequest Request { get; }

    public SimpleHttpResponse Response { get; }
}

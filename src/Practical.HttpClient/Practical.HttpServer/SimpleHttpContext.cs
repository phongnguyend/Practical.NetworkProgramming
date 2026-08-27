namespace Practical.HttpServer;

public sealed class SimpleHttpContext
{
    internal SimpleHttpContext(SimpleHttpRequest request, SimpleHttpResponse response)
    {
        Request = request;
        Response = response;
    }

    public SimpleHttpRequest Request { get; }

    public SimpleHttpResponse Response { get; }
}

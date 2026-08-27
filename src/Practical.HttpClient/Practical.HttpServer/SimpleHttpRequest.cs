namespace Practical.HttpServer;

public sealed class SimpleHttpRequest
{
    internal SimpleHttpRequest(
        string method,
        string version,
        string path,
        string queryString,
        SimpleHttpHeaders headers)
    {
        Method = method;
        Version = version;
        Path = path;
        QueryString = queryString;
        Headers = headers;
        Body = Stream.Null;
        Content = new SimpleHttpContent(Body, headers);
    }

    public string Method { get; }

    public string Version { get; }

    public string Path { get; }

    public string QueryString { get; }

    public SimpleHttpHeaders Headers { get; }

    public Stream Body { get; private set; }

    public SimpleHttpContent Content { get; private set; }

    internal void SetBody(Stream body)
    {
        Body = body;
        Content = new SimpleHttpContent(body, Headers);
    }
}

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
        Content = new SimpleHttpContent(Stream.Null, headers);
    }

    public string Method { get; }

    public string Version { get; }

    public string Path { get; }

    public string QueryString { get; }

    public SimpleHttpHeaders Headers { get; }

    public SimpleHttpContent Content { get; private set; }

    internal void SetContent(Stream body)
    {
        Content = new SimpleHttpContent(body, Headers);
    }
}

using System.Net.Sockets;

namespace Practical.HttpClient;

public sealed class SimpleHttpResponse : IDisposable, IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private bool _disposed;

    internal SimpleHttpResponse(
        TcpClient client,
        Stream stream,
        Uri requestUri,
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string> headers)
    {
        _client = client;
        _stream = stream;
        RequestUri = requestUri;
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Headers = new SimpleHttpHeaders(headers);
        Content = new SimpleHttpContent(stream, Headers);
    }

    public int StatusCode { get; }

    public Uri RequestUri { get; }

    public string ReasonPhrase { get; }

    public SimpleHttpHeaders Headers { get; }

    public SimpleHttpContent Content { get; }

    public bool IsSuccessStatusCode => StatusCode is >= 200 and < 300;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Content.MarkDisposed();
        _stream.Dispose();
        _client.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        Content.MarkDisposed();
        await _stream.DisposeAsync();
        _client.Dispose();
    }
}

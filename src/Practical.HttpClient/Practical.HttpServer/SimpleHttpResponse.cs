using System.Net.Sockets;
using System.Text;

namespace Practical.HttpServer;

public sealed class SimpleHttpResponse
{
    private readonly NetworkStream _networkStream;
    private readonly ChunkedWriteStream _chunkedStream;
    private readonly Stream _body;
    private int _statusCode = 200;
    private string _reasonPhrase = "OK";

    internal SimpleHttpResponse(NetworkStream networkStream)
    {
        _networkStream = networkStream;
        _chunkedStream = new ChunkedWriteStream(networkStream);
        _body = new ResponseBodyStream(this);
    }

    public int StatusCode
    {
        get => _statusCode;
        set
        {
            EnsureNotStarted();
            if (value is < 100 or > 999)
                throw new ArgumentOutOfRangeException(nameof(value));
            _statusCode = value;
        }
    }

    public string ReasonPhrase
    {
        get => _reasonPhrase;
        set
        {
            EnsureNotStarted();
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _reasonPhrase = value;
        }
    }

    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Stream Body => _body;

    public bool HasStarted { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (HasStarted)
            return;

        HasStarted = true;
        var response = new StringBuilder();
        response.Append($"HTTP/1.1 {StatusCode} {ReasonPhrase}\r\n");
        foreach (var header in Headers)
        {
            if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                response.Append($"{header.Key}: {header.Value}\r\n");
            }
        }
        response.Append("Transfer-Encoding: chunked\r\n");
        response.Append("Connection: close\r\n\r\n");

        await _networkStream.WriteAsync(
            Encoding.ASCII.GetBytes(response.ToString()),
            cancellationToken);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);
        await _chunkedStream.CompleteAsync(cancellationToken);
    }

    private void EnsureNotStarted()
    {
        if (HasStarted)
            throw new InvalidOperationException("The response has already started.");
    }

    private sealed class ResponseBodyStream(SimpleHttpResponse response) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            response.StartAsync().GetAwaiter().GetResult();
            response._chunkedStream.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await response.StartAsync(cancellationToken);
            await response._chunkedStream.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => response._networkStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            response._networkStream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

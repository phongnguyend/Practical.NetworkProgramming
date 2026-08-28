namespace Practical.HttpServer;

public sealed class SimpleHttpServerOptions
{
    private int _maximumRequestHeaderSize = 64 * 1024;
    private int _maximumRequestBodySize = 10 * 1024 * 1024;
    private int _maximumWebSocketMessageSize = 1024 * 1024;
    private TimeSpan _webSocketKeepAliveInterval = TimeSpan.FromSeconds(30);

    public int MaximumRequestHeaderSize
    {
        get => _maximumRequestHeaderSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maximumRequestHeaderSize = value;
        }
    }

    public int MaximumRequestBodySize
    {
        get => _maximumRequestBodySize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            _maximumRequestBodySize = value;
        }
    }

    public IReadOnlyCollection<string> AllowedMethods { get; init; } =
        ["GET", "POST", "PUT", "DELETE"];

    public int MaximumWebSocketMessageSize
    {
        get => _maximumWebSocketMessageSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maximumWebSocketMessageSize = value;
        }
    }

    public TimeSpan WebSocketKeepAliveInterval
    {
        get => _webSocketKeepAliveInterval;
        init
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), "The keep-alive interval must be positive.");
            _webSocketKeepAliveInterval = value;
        }
    }
}

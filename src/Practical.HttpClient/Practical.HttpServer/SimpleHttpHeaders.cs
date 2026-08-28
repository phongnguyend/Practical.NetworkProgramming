using System.Collections;
using System.Globalization;
using System.Net.Http.Headers;

namespace Practical.HttpServer;

public sealed class SimpleHttpHeaders : IReadOnlyDictionary<string, string>
{
    private readonly IReadOnlyDictionary<string, string> _headers;

    internal SimpleHttpHeaders(IReadOnlyDictionary<string, string> headers)
    {
        _headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);

        Host = GetValueOrDefault("Host");
        ContentLength = ParseContentLength();
        ContentType = Parse<MediaTypeHeaderValue>("Content-Type", MediaTypeHeaderValue.TryParse);
        Authorization = Parse<AuthenticationHeaderValue>("Authorization", AuthenticationHeaderValue.TryParse);
        Accept = ParseList("Accept");
        AcceptEncoding = ParseList("Accept-Encoding");
        Connection = ParseList("Connection");
        TransferEncoding = ParseList("Transfer-Encoding");
        Upgrade = GetValueOrDefault("Upgrade");
        UserAgent = GetValueOrDefault("User-Agent");
        WebSocketKey = GetValueOrDefault("Sec-WebSocket-Key");
        WebSocketVersion = GetValueOrDefault("Sec-WebSocket-Version");
    }

    public string? Host { get; }

    public long? ContentLength { get; }

    public MediaTypeHeaderValue? ContentType { get; }

    public AuthenticationHeaderValue? Authorization { get; }

    public IReadOnlyList<string> Accept { get; }

    public IReadOnlyList<string> AcceptEncoding { get; }

    public IReadOnlyList<string> Connection { get; }

    public IReadOnlyList<string> TransferEncoding { get; }

    public string? Upgrade { get; }

    public string? UserAgent { get; }

    public string? WebSocketKey { get; }

    public string? WebSocketVersion { get; }

    public int Count => _headers.Count;

    public IEnumerable<string> Keys => _headers.Keys;

    public IEnumerable<string> Values => _headers.Values;

    public string this[string name] => _headers[name];

    public bool ContainsKey(string name) => _headers.ContainsKey(name);

    public bool TryGetValue(string name, out string value) => _headers.TryGetValue(name, out value!);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _headers.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool ContainsToken(string name, string token) =>
        _headers.TryGetValue(name, out string? value) &&
        value.Split(',').Any(part => part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));

    public bool IsWebSocketAttempt() =>
        ContainsToken("Upgrade", "websocket") || WebSocketKey is not null;

    public bool TryGetContentLength(out int contentLength, out string error)
    {
        contentLength = 0;
        error = string.Empty;

        if (TransferEncoding.Count > 0)
        {
            error = "Transfer-Encoding is not supported; send a Content-Length header instead.";
            return false;
        }

        if (!ContainsKey("Content-Length"))
            return true;

        if (ContentLength is not long parsedContentLength || parsedContentLength > int.MaxValue)
        {
            error = "Content-Length must be a non-negative integer.";
            return false;
        }

        contentLength = (int)parsedContentLength;
        return true;
    }

    private string? GetValueOrDefault(string name) =>
        _headers.TryGetValue(name, out string? value) ? value : null;

    private long? ParseContentLength() =>
        _headers.TryGetValue("Content-Length", out string? value) &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) &&
        result >= 0
            ? result
            : null;

    private IReadOnlyList<string> ParseList(string name) =>
        _headers.TryGetValue(name, out string? value)
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

    private T? Parse<T>(string name, TryParseHeader<T> parser) where T : class =>
        _headers.TryGetValue(name, out string? value) && parser(value, out T? result)
            ? result
            : null;

    private delegate bool TryParseHeader<T>(string? value, out T? result) where T : class;
}

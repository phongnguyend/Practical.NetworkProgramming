using System.Collections;
using System.Globalization;
using System.Net.Http.Headers;

namespace Practical.HttpClient;

public sealed class SimpleHttpHeaders : IReadOnlyDictionary<string, string>
{
    private readonly IReadOnlyDictionary<string, string> _headers;

    internal SimpleHttpHeaders(IReadOnlyDictionary<string, string> headers)
    {
        _headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);

        ContentLength = ParseInt64("Content-Length");
        ContentType = Parse<MediaTypeHeaderValue>("Content-Type", MediaTypeHeaderValue.TryParse);
        ContentDisposition = Parse<ContentDispositionHeaderValue>(
            "Content-Disposition",
            ContentDispositionHeaderValue.TryParse);
        Location = ParseUri("Location");
        Date = ParseDate("Date");
        LastModified = ParseDate("Last-Modified");
        ETag = Parse<EntityTagHeaderValue>("ETag", EntityTagHeaderValue.TryParse);
        RetryAfter = Parse<RetryConditionHeaderValue>("Retry-After", RetryConditionHeaderValue.TryParse);
        ContentEncoding = ParseList("Content-Encoding");
        TransferEncoding = ParseList("Transfer-Encoding");
        Connection = ParseList("Connection");
        Server = GetValueOrDefault("Server");
    }

    public long? ContentLength { get; }

    public MediaTypeHeaderValue? ContentType { get; }

    public ContentDispositionHeaderValue? ContentDisposition { get; }

    public IReadOnlyList<string> ContentEncoding { get; }

    public Uri? Location { get; }

    public DateTimeOffset? Date { get; }

    public DateTimeOffset? LastModified { get; }

    public EntityTagHeaderValue? ETag { get; }

    public RetryConditionHeaderValue? RetryAfter { get; }

    public IReadOnlyList<string> TransferEncoding { get; }

    public IReadOnlyList<string> Connection { get; }

    public string? Server { get; }

    public int Count => _headers.Count;

    public IEnumerable<string> Keys => _headers.Keys;

    public IEnumerable<string> Values => _headers.Values;

    public string this[string name] => _headers[name];

    public bool ContainsKey(string name) => _headers.ContainsKey(name);

    public bool TryGetValue(string name, out string value) => _headers.TryGetValue(name, out value!);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _headers.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private string? GetValueOrDefault(string name) =>
        _headers.TryGetValue(name, out string? value) ? value : null;

    private long? ParseInt64(string name) =>
        _headers.TryGetValue(name, out string? value) &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) &&
        result >= 0
            ? result
            : null;

    private DateTimeOffset? ParseDate(string name) =>
        _headers.TryGetValue(name, out string? value) &&
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset result)
                ? result
                : null;

    private Uri? ParseUri(string name) =>
        _headers.TryGetValue(name, out string? value) &&
        Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out Uri? result)
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

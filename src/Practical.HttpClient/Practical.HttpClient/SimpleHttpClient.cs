using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Practical.HttpClient;

public class SimpleHttpClient
{
    public Task<string> GetAsync(string url)
        => SendAsync("GET", url);

    public Task<string> PostAsync(string url, string? body = null, string contentType = "application/json")
        => SendAsync("POST", url, body, contentType);

    public Task<string> PutAsync(string url, string? body = null, string contentType = "application/json")
        => SendAsync("PUT", url, body, contentType);

    public Task<string> DeleteAsync(string url)
        => SendAsync("DELETE", url);

    private static async Task<string> SendAsync(string method, string url, string? body = null, string contentType = "application/json")
    {
        var uri = new Uri(url);
        string host = uri.Host;
        bool https = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        string path = uri.PathAndQuery;

        using var client = new TcpClient();
        await client.ConnectAsync(host, uri.Port);

        using var stream = await GetStreamAsync(client, host, https);

        byte[]? bodyBytes = body is not null ? Encoding.UTF8.GetBytes(body) : null;

        var sb = new StringBuilder();
        sb.Append($"{method} {path} HTTP/1.1\r\n");
        sb.Append($"Host: {host}\r\n");
        if (bodyBytes is not null)
        {
            sb.Append($"Content-Type: {contentType}\r\n");
            sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
        }
        sb.Append("Connection: close\r\n\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes);
        if (bodyBytes is not null)
            await stream.WriteAsync(bodyBytes);

        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync();
    }

    private static async Task<Stream> GetStreamAsync(TcpClient client, string host, bool https)
    {
        if (https)
        {
            var sslStream = new SslStream(client.GetStream(), false, (sender, cert, chain, errors) => true);
            await sslStream.AuthenticateAsClientAsync(host);
            return sslStream;
        }

        return client.GetStream();
    }
}

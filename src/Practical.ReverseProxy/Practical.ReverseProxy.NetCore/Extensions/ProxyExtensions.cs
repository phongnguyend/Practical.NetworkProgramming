using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Practical.ReverseProxy.NetCore.Extensions;

public static class ProxyExtensions
{
    private const int StreamCopyBufferSize = 81920;

    private static readonly HttpClient _httpClient = new HttpClient();

    private static HttpRequestMessage CloneRequest(this HttpContext httpContext, Uri uri)
    {
        var request = httpContext.Request;

        var requestMessage = new HttpRequestMessage();
        var requestMethod = request.Method;
        if (!HttpMethods.IsGet(requestMethod) &&
            !HttpMethods.IsHead(requestMethod) &&
            !HttpMethods.IsDelete(requestMethod) &&
            !HttpMethods.IsTrace(requestMethod))
        {
            request.Body.Seek(0, SeekOrigin.Begin);
            var streamContent = new StreamContent(request.Body);
            requestMessage.Content = streamContent;
        }

        foreach (var header in request.Headers)
        {
            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && requestMessage.Content != null)
            {
                requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        requestMessage.Headers.Host = uri.Authority;
        requestMessage.RequestUri = uri;
        requestMessage.Method = new HttpMethod(request.Method);

        return requestMessage;
    }

    private static async Task ForwardResponseAsync(this HttpContext httpContext, HttpResponseMessage httpResponseMessage)
    {
        var response = httpContext.Response;

        response.StatusCode = (int)httpResponseMessage.StatusCode;

        foreach (var header in httpResponseMessage.Headers)
        {
            response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in httpResponseMessage.Content.Headers)
        {
            response.Headers[header.Key] = header.Value.ToArray();
        }

        response.Headers.Remove("transfer-encoding");

        using var responseStream = await httpResponseMessage.Content.ReadAsStreamAsync();
        await responseStream.CopyToAsync(response.Body, StreamCopyBufferSize, httpContext.RequestAborted);
    }

    public static async Task ProxyAsync(this HttpContext httpContext, string url)
    {
        var request = httpContext.CloneRequest(new Uri(url));
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await httpContext.ForwardResponseAsync(response);
    }
}

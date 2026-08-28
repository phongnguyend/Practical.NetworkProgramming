using System.Net;
using Practical.HttpServer;

var port = 8080;

if (args.Length > 0 && (!int.TryParse(args[0], out port) || port is < 1 or > 65535))
{
    Console.Error.WriteLine("Usage: dotnet run --project Practical.HttpServer -- [port]");
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var server = new SimpleHttpServer(IPAddress.Any, port)
{
    RequestHandler = (context, cancellationToken) =>
    {
        var remoteEndPoint = context.Socket.RemoteEndPoint as IPEndPoint;
        var clientInformation = new
        {
            context.Request.Method,
            Host = context.Request.Headers.Host ?? string.Empty,
            Ip = remoteEndPoint?.Address.ToString() ?? string.Empty,
            context.Request.Path,
            context.Request.QueryString,
            context.Request.Headers,
            BodyLength = context.Request.Content.Length ?? 0
        };

        return context.Response.WriteJsonAsync(clientInformation, cancellationToken);
    }
};
Console.WriteLine($"HTTP server listening on http://localhost:{port}/");
Console.WriteLine($"WebSocket echo endpoint: ws://localhost:{port}/ws");
Console.WriteLine("Press Ctrl+C to stop.");

await server.RunAsync(cancellation.Token);
return 0;

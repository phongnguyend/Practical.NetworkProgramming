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

var server = new SimpleHttpServer(IPAddress.Any, port);
Console.WriteLine($"HTTP server listening on http://localhost:{port}/");
Console.WriteLine("Press Ctrl+C to stop.");

await server.RunAsync(cancellation.Token);
return 0;

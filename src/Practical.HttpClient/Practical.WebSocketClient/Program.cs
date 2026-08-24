using System.Net.WebSockets;
using System.Text;
using Practical.WebSocketClient;

const string defaultEndpoint = "ws://localhost:8080/ws";

string endpointArgument = args.FirstOrDefault(argument => !argument.StartsWith("--")) ?? defaultEndpoint;
bool selfTest = args.Contains("--self-test", StringComparer.OrdinalIgnoreCase);

if (!Uri.TryCreate(endpointArgument, UriKind.Absolute, out Uri? endpoint) ||
    endpoint.Scheme is not ("ws" or "wss"))
{
    Console.Error.WriteLine("Usage: dotnet run --project Practical.WebSocketClient -- [ws-url] [--self-test]");
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    await using var client = new SimpleWebSocketClient();
    Console.WriteLine($"Connecting to {endpoint} ...");
    await client.ConnectAsync(endpoint, cancellation.Token);
    Console.WriteLine("Connected.");

    if (selfTest)
    {
        await RunSelfTestAsync(client, cancellation.Token);
    }
    else
    {
        await RunInteractiveAsync(client, cancellation.Token);
    }

    await client.CloseAsync(cancellation.Token);
    Console.WriteLine("Connection closed cleanly.");
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Cancelled.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"WebSocket client failed: {exception.Message}");
    return 1;
}

static async Task RunSelfTestAsync(SimpleWebSocketClient client, CancellationToken cancellationToken)
{
    const string text = "Hello, WebSocket!";
    string textEcho = await client.EchoTextAsync(text, cancellationToken);
    EnsureEqual(text, textEcho, "text echo");
    Console.WriteLine($"PASS text echo: {textEcho}");

    byte[] binary = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF];
    byte[] binaryEcho = await client.EchoBinaryAsync(binary, cancellationToken);
    EnsureSequenceEqual(binary, binaryEcho, "binary echo");
    Console.WriteLine($"PASS binary echo: {Convert.ToHexString(binaryEcho)}");

    const string fragmented = "This message was sent in two WebSocket fragments.";
    string fragmentedEcho = await client.EchoFragmentedTextAsync(fragmented, cancellationToken);
    EnsureEqual(fragmented, fragmentedEcho, "fragmented text echo");
    Console.WriteLine($"PASS fragmented text echo: {fragmentedEcho}");
}

static async Task RunInteractiveAsync(SimpleWebSocketClient client, CancellationToken cancellationToken)
{
    Console.WriteLine("Enter text to echo, '/binary <text>' to send binary data, or '/quit' to exit.");

    while (!cancellationToken.IsCancellationRequested && client.State == WebSocketState.Open)
    {
        Console.Write("> ");
        string? input = Console.ReadLine();

        if (input is null || input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
            break;

        if (input.StartsWith("/binary ", StringComparison.OrdinalIgnoreCase))
        {
            byte[] response = await client.EchoBinaryAsync(
                Encoding.UTF8.GetBytes(input[8..]),
                cancellationToken);
            Console.WriteLine($"Binary echo ({response.Length} bytes): {Convert.ToHexString(response)}");
        }
        else
        {
            string response = await client.EchoTextAsync(input, cancellationToken);
            Console.WriteLine($"Text echo: {response}");
        }
    }
}

static void EnsureEqual<T>(T expected, T actual, string testName)
    where T : IEquatable<T>
{
    if (!expected.Equals(actual))
        throw new InvalidDataException($"Failed {testName}: expected '{expected}', received '{actual}'.");
}

static void EnsureSequenceEqual(byte[] expected, byte[] actual, string testName)
{
    if (!expected.AsSpan().SequenceEqual(actual))
        throw new InvalidDataException($"Failed {testName}: echoed bytes do not match.");
}

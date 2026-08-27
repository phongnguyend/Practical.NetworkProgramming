using Practical.HttpClient;

var client = new SimpleHttpClient(new SimpleHttpClientOptions
{
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 10
});

await using var response = await client.GetAsync("https://github.com");
foreach (var header in response.Headers)
{
    Console.WriteLine($"{header.Key}: {header.Value}");
}
Console.WriteLine(await response.ReadAsStringAsync());

await using var response2 = await client.GetAsync("https://github.com/phongnguyend/CryptographyHelper/archive/refs/tags/releases/v4.0.0.zip");
foreach (var header in response2.Headers)
{
    Console.WriteLine($"{header.Key}: {header.Value}");
}
await response2.SaveContentToFileAsync("CryptographyHelper.zip");

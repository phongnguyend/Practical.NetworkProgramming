using Practical.HttpClient;
using System.Net.Http.Headers;
using System.Text;

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
Console.WriteLine($"Content-Type: {response.Content.Headers.ContentType}");
Console.WriteLine(await response.Content.ReadAsStringAsync());

await using var response2 = await client.GetAsync("https://github.com/phongnguyend/CryptographyHelper/archive/refs/tags/releases/v4.0.0.zip");
foreach (var header in response2.Headers)
{
    Console.WriteLine($"{header.Key}: {header.Value}");
}
await response2.Content.SaveToFileAsync("CryptographyHelper.zip");

Console.WriteLine();

using var form = new MultipartFormDataContent();
using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Test file content"));
fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
form.Add(fileContent, "file", "abc.txt");
form.Add(new StringContent("Test Name"), "name");

await using var response3 = await client.PostAsync($"http://localhost:8000/convert", form);
foreach (var header in response3.Headers)
{
    Console.WriteLine($"{header.Key}: {header.Value}");
}
Console.WriteLine(await response3.Content.ReadAsStringAsync());


using Practical.HttpClient;

var client = new SimpleHttpClient();

var response = await client.GetAsync("https://github.com");

Console.WriteLine(response);
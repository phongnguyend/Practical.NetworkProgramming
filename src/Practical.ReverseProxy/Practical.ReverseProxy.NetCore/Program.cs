using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using Practical.ReverseProxy.NetCore.Extensions;
using UriHelper;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseCors(configurePolicy =>
{
    configurePolicy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Minimal API example using ProxyExtensions
app.Map("{**catchAll}", async (HttpContext context, string catchAll) =>
{
    await context.ProxyAsync(UriPath.Combine("https://localhost:44352", $"{catchAll}{context.Request.QueryString}"));
});

app.Run();

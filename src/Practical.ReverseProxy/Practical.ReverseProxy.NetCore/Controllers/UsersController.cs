using Microsoft.AspNetCore.Mvc;
using Practical.ReverseProxy.NetCore.Extensions;
using System.Threading.Tasks;

namespace Practical.ReverseProxy.NetCore.Controllers;

[Route("api/[controller]")]
[ApiController]
public class UsersController : ControllerBase
{
    [HttpGet]
    public async Task Get()
    {
        await HttpContext.ProxyAsync("https://localhost:44352/api/users");
    }

    [HttpPost]
    public async Task Post(object model)
    {
        await HttpContext.ProxyAsync("https://localhost:44352/api/users");
    }

    [HttpPut("{id}")]
    public async Task Put(string id, object model)
    {
        await HttpContext.ProxyAsync($"https://localhost:44352/api/users/{id}");
    }

    [HttpDelete("{id}")]
    public async Task Delete(string id)
    {
        await HttpContext.ProxyAsync($"https://localhost:44352/api/users/{id}");
    }
}

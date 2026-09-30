using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Hosting.Https;

namespace WreckfestController.Controllers;

/// <summary>Whether the API serves HTTPS, and with which certificate. No key material, password or path.</summary>
[ApiController]
[Authorize(Policy = ApiAuthentication.AdminPolicy)]
[Route("api/https")]
public sealed class HttpsController(HttpsStatusSource source) : ControllerBase
{
    [HttpGet]
    public HttpsStatus Get() => source.Status;
}

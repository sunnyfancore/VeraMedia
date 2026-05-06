using Microsoft.AspNetCore.Mvc;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Services;

namespace VeraMedia.Api.Controllers;

[ApiController]
[Route("api/app")]
public sealed class AppConfigController(IConfiguration configuration, IAppSettingsService appSettingsService) : ControllerBase
{
    [HttpGet("config")]
    public async Task<ActionResult<AppConfigResponse>> Get(CancellationToken cancellationToken)
    {
        var seed = configuration.GetSection("SeedUser").Get<SeedUserOptions>() ?? new SeedUserOptions();
        var settings = await appSettingsService.GetAuthEmailSettingsAsync(cancellationToken);
        return Ok(new AppConfigResponse(
            new SeedUserConfig(seed.Enabled, seed.Email, seed.Password, seed.DisplayName),
            new PublicAuthConfig(settings.AllowRegistration, settings.RequireEmailCode)));
    }
}

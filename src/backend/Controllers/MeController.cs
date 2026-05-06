using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeraMedia.Api.Contracts;

namespace VeraMedia.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/me")]
public sealed class MeController : ControllerBase
{
    [HttpGet]
    public ActionResult<UserProfile> Get()
    {
        return Ok(new UserProfile(
            User.GetUserId(),
            User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value ?? "",
            User.FindFirst("display_name")?.Value ?? "",
            User.IsAdmin()));
    }
}

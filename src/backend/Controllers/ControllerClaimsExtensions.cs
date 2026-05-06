using System.Security.Claims;

namespace VeraMedia.Api.Controllers;

internal static class ControllerClaimsExtensions
{
    public static long GetUserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) ? id : throw new UnauthorizedAccessException();
    }

    public static DateTime GetTokenExpiresAt(this ClaimsPrincipal user)
    {
        var value = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Exp)?.Value
            ?? user.FindFirst("exp")?.Value;
        if (!long.TryParse(value, out var seconds))
        {
            throw new UnauthorizedAccessException();
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

    public static DateTime GetTokenRefreshableUntil(this ClaimsPrincipal user)
    {
        var value = user.FindFirst("refresh_until")?.Value;
        if (!long.TryParse(value, out var seconds))
        {
            throw new UnauthorizedAccessException();
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

    public static bool IsAdmin(this ClaimsPrincipal user)
    {
        return string.Equals(user.FindFirst("is_admin")?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }
}

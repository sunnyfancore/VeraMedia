namespace VeraMedia.Api.Services;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "VeraMedia";
    public string Audience { get; set; } = "VeraMedia.Client";
    public string SigningKey { get; set; } = "replace-this-development-key-with-at-least-32-chars";
    public int AccessTokenMinutes { get; set; } = 120;
    public int RefreshWindowMinutes { get; set; } = 10080;
}

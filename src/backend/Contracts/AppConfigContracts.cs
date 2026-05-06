namespace VeraMedia.Api.Contracts;

public sealed record AppConfigResponse(SeedUserConfig SeedUser, PublicAuthConfig Auth);
public sealed record SeedUserConfig(bool Enabled, string Email, string Password, string DisplayName);
public sealed record PublicAuthConfig(bool AllowRegistration, bool RequireEmailCode);

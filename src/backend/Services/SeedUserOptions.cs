namespace VeraMedia.Api.Services;

public sealed class SeedUserOptions
{
    public bool Enabled { get; set; } = true;
    public string Email { get; set; } = "demo@veramedia.local";
    public string Password { get; set; } = "12345678";
    public string DisplayName { get; set; } = "运营同学";
}

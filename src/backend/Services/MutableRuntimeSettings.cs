namespace VeraMedia.Api.Services;

public sealed class MutableRuntimeSettings
{
    public AuthOptions Auth { get; } = new();
    public EmailCodeOptions EmailCode { get; } = new();
}

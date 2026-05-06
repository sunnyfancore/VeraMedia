namespace VeraMedia.Api.Models;

public sealed class AiProvider
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ProviderType { get; set; } = "openai-compatible";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<AiModel> Models { get; set; } = [];
}

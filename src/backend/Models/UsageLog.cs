namespace VeraMedia.Api.Models;

public sealed class UsageLog
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public string Operation { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

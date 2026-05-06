namespace VeraMedia.Api.Models;

public sealed class ArticleShare
{
    public long Id { get; set; }
    public long ArticleId { get; set; }
    public Article? Article { get; set; }
    public string Token { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
}

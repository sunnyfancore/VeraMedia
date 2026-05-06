namespace VeraMedia.Api.Models;

public sealed class ContentProject
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public string Title { get; set; } = "未命名选题";
    public string? SourceUrl { get; set; }
    public string Status { get; set; } = "draft";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Article> Articles { get; set; } = [];
    public List<GeneratedImage> Images { get; set; } = [];
}

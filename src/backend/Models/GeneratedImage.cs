namespace VeraMedia.Api.Models;

public sealed class GeneratedImage
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public ContentProject? Project { get; set; }
    public string Prompt { get; set; } = "";
    public string? ImageUrl { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

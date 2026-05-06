namespace VeraMedia.Api.Models;

public sealed class ConversationMessage
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public Conversation? Conversation { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string MetadataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

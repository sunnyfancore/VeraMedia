namespace VeraMedia.Api.Models;

public sealed class Conversation
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public string Title { get; set; } = "新的内容任务";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ConversationMessage> Messages { get; set; } = [];
}

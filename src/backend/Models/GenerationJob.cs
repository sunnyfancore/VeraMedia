namespace VeraMedia.Api.Models;

public sealed class GenerationJob
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public User? User { get; set; }
    public long ConversationId { get; set; }
    public Conversation? Conversation { get; set; }
    public long UserMessageId { get; set; }
    public long AssistantMessageId { get; set; }
    public ConversationMessage? AssistantMessage { get; set; }
    public string JobType { get; set; } = GenerationJobTypes.Conversation;
    public string Status { get; set; } = GenerationJobStatuses.Pending;
    public string RequestJson { get; set; } = "{}";
    public string Content { get; set; } = "";
    public string ThinkingJson { get; set; } = "[]";
    public string? MessageType { get; set; }
    public string? Error { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class GenerationJobTypes
{
    public const string Conversation = "conversation";
}

public static class GenerationJobStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    public static bool IsTerminal(string status) => status is Completed or Failed or Canceled;
}

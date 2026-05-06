namespace VeraMedia.Api.Models;

public sealed class AuditLog
{
    public long Id { get; set; }
    public long? ActorUserId { get; set; }
    public string Operation { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Detail { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

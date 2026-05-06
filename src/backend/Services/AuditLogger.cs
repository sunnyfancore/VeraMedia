using VeraMedia.Api.Data;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public sealed class AuditLogger(AppDbContext db, IHttpContextAccessor httpContextAccessor) : IAuditLogger
{
    public async Task LogAsync(
        long? actorUserId,
        string operation,
        string entityType,
        string entityId,
        string detail,
        CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext;
        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorUserId,
            Operation = operation.Trim().Clamp(80),
            EntityType = entityType.Trim().Clamp(80),
            EntityId = entityId.Trim().Clamp(80),
            Detail = detail.Trim().Clamp(1000),
            IpAddress = http?.Connection.RemoteIpAddress?.ToString().Clamp(80) ?? "",
            UserAgent = http?.Request.Headers.UserAgent.ToString().Clamp(300) ?? "",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

file static class AuditStringExtensions
{
    public static string Clamp(this string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

namespace VeraMedia.Api.Services;

public interface IAuditLogger
{
    Task LogAsync(
        long? actorUserId,
        string operation,
        string entityType,
        string entityId,
        string detail,
        CancellationToken cancellationToken);
}

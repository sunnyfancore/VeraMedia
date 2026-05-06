namespace VeraMedia.Api.Contracts;

public sealed record AdminUserDto(long Id, string Email, string DisplayName, bool IsAdmin, bool IsEnabled, DateTime CreatedAt);
public sealed record AdminUpdateUserRequest(string DisplayName, bool IsAdmin, bool IsEnabled);
public sealed record AdminCreateUserRequest(string Email, string Password, string DisplayName, bool IsAdmin, bool IsEnabled);
public sealed record AdminResetPasswordRequest(string NewPassword);
public sealed record AdminRuntimeConfigDto(
    bool AllowRegistration,
    bool RequireEmailCode,
    bool EmailCodeEnabled,
    string SmtpHost,
    int SmtpPort,
    bool EnableSsl,
    string UserName,
    string FromEmail,
    string FromName,
    int CodeMinutes);

public sealed record AdminRuntimeConfigRequest(
    bool AllowRegistration,
    bool RequireEmailCode,
    bool EmailCodeEnabled,
    string? SmtpHost,
    int SmtpPort,
    bool EnableSsl,
    string? UserName,
    string? Password,
    string? FromEmail,
    string? FromName,
    int CodeMinutes);

public sealed record AdminPromptConfigDto(
    string Global,
    string Chat,
    string Article,
    string Document,
    string Image,
    string Rewrite,
    string Layout);

public sealed record AdminDashboardStatsDto(
    int TotalUsers,
    int EnabledUsers,
    int TotalConversations,
    int TotalJobs,
    int PendingJobs,
    int RunningJobs,
    int CompletedJobs,
    int FailedJobs,
    int CanceledJobs,
    int StaleJobs,
    int ArticleAssets,
    int ImageAssets,
    IReadOnlyList<AdminRecentFailureDto> RecentFailures,
    IReadOnlyList<AdminIntentLogDto> RecentIntents,
    IReadOnlyList<AdminAuditLogDto> RecentAudits);

public sealed record AdminRecentFailureDto(
    long Id,
    long ConversationId,
    string ConversationTitle,
    string JobType,
    string? MessageType,
    string Error,
    DateTime UpdatedAt);

public sealed record AdminIntentLogDto(
    long JobId,
    long ConversationId,
    string ConversationTitle,
    string RequestPreview,
    string? MessageType,
    string Status,
    DateTime UpdatedAt);

public sealed record AdminAuditLogDto(
    long Id,
    string ActorName,
    string Operation,
    string EntityType,
    string EntityId,
    string Detail,
    DateTime CreatedAt);

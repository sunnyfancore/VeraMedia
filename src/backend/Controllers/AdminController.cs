using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;
using VeraMedia.Api.Services;

namespace VeraMedia.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin")]
public sealed class AdminController(
    AppDbContext db,
    IAppSettingsService appSettingsService,
    IAuditLogger auditLogger) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<AdminDashboardStatsDto>> Dashboard(CancellationToken cancellationToken)
    {
        var staleBefore = DateTime.UtcNow.AddMinutes(-20);
        var staleJobsTask = db.GenerationJobs
            .CountAsync(x => (x.Status == GenerationJobStatuses.Pending || x.Status == GenerationJobStatuses.Running) && x.UpdatedAt < staleBefore, cancellationToken);
        var totalUsersTask = db.Users.CountAsync(cancellationToken);
        var enabledUsersTask = db.Users.CountAsync(x => x.IsEnabled, cancellationToken);
        var totalConversationsTask = db.Conversations.CountAsync(cancellationToken);
        var totalJobsTask = db.GenerationJobs.CountAsync(cancellationToken);
        var pendingJobsTask = db.GenerationJobs.CountAsync(x => x.Status == GenerationJobStatuses.Pending, cancellationToken);
        var runningJobsTask = db.GenerationJobs.CountAsync(x => x.Status == GenerationJobStatuses.Running, cancellationToken);
        var completedJobsTask = db.GenerationJobs.CountAsync(x => x.Status == GenerationJobStatuses.Completed, cancellationToken);
        var failedJobsTask = db.GenerationJobs.CountAsync(x => x.Status == GenerationJobStatuses.Failed, cancellationToken);
        var canceledJobsTask = db.GenerationJobs.CountAsync(x => x.Status == GenerationJobStatuses.Canceled, cancellationToken);
        var articleAssetsTask = db.Articles.CountAsync(cancellationToken);
        var imageAssetsTask = db.GeneratedImages.CountAsync(cancellationToken);

        await Task.WhenAll(
            staleJobsTask, totalUsersTask, enabledUsersTask, totalConversationsTask,
            totalJobsTask, pendingJobsTask, runningJobsTask, completedJobsTask,
            failedJobsTask, canceledJobsTask, articleAssetsTask, imageAssetsTask);

        var recentFailures = await db.GenerationJobs
            .AsNoTracking()
            .Include(x => x.Conversation)
            .Where(x => x.Status == GenerationJobStatuses.Failed)
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(8)
            .Select(x => new AdminRecentFailureDto(
                x.Id,
                x.ConversationId,
                x.Conversation == null ? "未命名会话" : x.Conversation.Title,
                x.JobType,
                x.MessageType,
                x.Error ?? "未知错误",
                x.UpdatedAt))
            .ToListAsync(cancellationToken);

        var recentIntentRows = await db.GenerationJobs
            .AsNoTracking()
            .Include(x => x.Conversation)
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(12)
            .Select(x => new
            {
                x.Id,
                x.ConversationId,
                ConversationTitle = x.Conversation == null ? "未命名会话" : x.Conversation.Title,
                x.RequestJson,
                x.MessageType,
                x.Status,
                x.UpdatedAt
            })
            .ToListAsync(cancellationToken);
        var recentIntents = recentIntentRows
            .Select(x => new AdminIntentLogDto(
                x.Id,
                x.ConversationId,
                x.ConversationTitle,
                BuildPreview(x.RequestJson),
                x.MessageType,
                x.Status,
                x.UpdatedAt))
            .ToList();

        var auditRows = await db.AuditLogs
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(12)
            .ToListAsync(cancellationToken);
        var auditUserIds = auditRows
            .Where(x => x.ActorUserId.HasValue)
            .Select(x => x.ActorUserId!.Value)
            .Distinct()
            .ToList();
        var auditUserNames = auditUserIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.Users
                .AsNoTracking()
                .Where(x => auditUserIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
        var recentAudits = auditRows
            .Select(x => new AdminAuditLogDto(
                x.Id,
                x.ActorUserId.HasValue && auditUserNames.TryGetValue(x.ActorUserId.Value, out var actorName) ? actorName : "系统",
                x.Operation,
                x.EntityType,
                x.EntityId,
                x.Detail,
                x.CreatedAt))
            .ToList();

        return Ok(new AdminDashboardStatsDto(
            totalUsersTask.Result,
            enabledUsersTask.Result,
            totalConversationsTask.Result,
            totalJobsTask.Result,
            pendingJobsTask.Result,
            runningJobsTask.Result,
            completedJobsTask.Result,
            failedJobsTask.Result,
            canceledJobsTask.Result,
            staleJobsTask.Result,
            articleAssetsTask.Result,
            imageAssetsTask.Result,
            recentFailures,
            recentIntents,
            recentAudits));
    }

    [HttpPost("jobs/mark-stale-failed")]
    public async Task<IActionResult> MarkStaleJobsFailed(CancellationToken cancellationToken)
    {
        var staleBefore = DateTime.UtcNow.AddMinutes(-20);
        var jobs = await db.GenerationJobs
            .Include(x => x.AssistantMessage)
            .Where(x => (x.Status == GenerationJobStatuses.Pending || x.Status == GenerationJobStatuses.Running) && x.UpdatedAt < staleBefore)
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.Status = GenerationJobStatuses.Failed;
            job.Error = "任务长时间未更新，已自动标记为失败。";
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            job.Version++;
            if (string.IsNullOrWhiteSpace(job.Content))
            {
                job.Content = "任务长时间未更新，已停止。请点击上一条消息重试。";
            }

            if (job.AssistantMessage is not null)
            {
                job.AssistantMessage.Content = job.Content;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(User.GetUserId(), "mark_stale_jobs_failed", "generation_jobs", "batch", $"处理 {jobs.Count} 个长时间未更新任务", cancellationToken);
        return Ok(new { count = jobs.Count });
    }

    [HttpGet("users")]
    public async Task<ActionResult<IReadOnlyList<AdminUserDto>>> Users(CancellationToken cancellationToken)
    {
        var rows = await db.Users
            .OrderByDescending(x => x.Id)
            .Select(x => new AdminUserDto(x.Id, x.Email, x.DisplayName, x.IsAdmin, x.IsEnabled, x.CreatedAt))
            .ToListAsync(cancellationToken);
        return Ok(rows);
    }

    [HttpPost("users")]
    public async Task<ActionResult<AdminUserDto>> CreateUser(AdminCreateUserRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Email == email, cancellationToken))
        {
            return BadRequest(new { message = "该邮箱已经存在。" });
        }

        var user = new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email.Split('@')[0] : request.DisplayName.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsAdmin = request.IsAdmin,
            IsEnabled = request.IsEnabled
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(User.GetUserId(), "create_user", "user", user.Id.ToString(), $"创建账号 {user.Email}", cancellationToken);
        return Ok(new AdminUserDto(user.Id, user.Email, user.DisplayName, user.IsAdmin, user.IsEnabled, user.CreatedAt));
    }

    [HttpPut("users/{id:long}")]
    public async Task<ActionResult<AdminUserDto>> UpdateUser(long id, AdminUpdateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null) return NotFound(new { message = "用户不存在。" });

        user.DisplayName = request.DisplayName.Trim();
        user.IsAdmin = request.IsAdmin;
        user.IsEnabled = request.IsEnabled;
        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(User.GetUserId(), "update_user", "user", user.Id.ToString(), $"更新账号 {user.Email}", cancellationToken);
        return Ok(new AdminUserDto(user.Id, user.Email, user.DisplayName, user.IsAdmin, user.IsEnabled, user.CreatedAt));
    }

    [HttpPost("users/{id:long}/password")]
    public async Task<IActionResult> ResetUserPassword(long id, AdminResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null) return NotFound(new { message = "用户不存在。" });
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(User.GetUserId(), "reset_password", "user", user.Id.ToString(), $"重置账号 {user.Email} 的密码", cancellationToken);
        return Ok(new { message = "密码已重置。" });
    }

    [HttpGet("users/{id:long}/provider")]
    public async Task<ActionResult<ProviderResponse>> UserProvider(long id, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(x => x.Id == id, cancellationToken)) return NotFound(new { message = "用户不存在。" });

        var provider = await db.AiProviders
            .Include(x => x.Models)
            .Where(x => x.UserId == id && x.Enabled)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(provider is null
            ? new ProviderResponse(0, "OpenAI", "", "", "", true, false, "")
            : AiProviderResponseMapper.ToResponse(provider));
    }

    [HttpPost("users/{id:long}/provider")]
    public async Task<ActionResult<ProviderResponse>> SaveUserProvider(long id, ProviderRequest request, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(x => x.Id == id, cancellationToken)) return NotFound(new { message = "用户不存在。" });

        var name = string.IsNullOrWhiteSpace(request.Name) ? "OpenAI" : request.Name.Trim();
        var provider = await db.AiProviders
            .Include(x => x.Models)
            .FirstOrDefaultAsync(x => x.UserId == id && x.Name == name, cancellationToken);

        if (provider is null)
        {
            provider = new AiProvider { UserId = id, Name = name, Enabled = true };
            db.AiProviders.Add(provider);
        }

        var otherProviders = await db.AiProviders
            .Where(x => x.UserId == id && x.Id != provider.Id)
            .ToListAsync(cancellationToken);
        foreach (var other in otherProviders)
        {
            other.Enabled = false;
        }

        provider.BaseUrl = request.BaseUrl.Trim().TrimEnd('/');
        provider.Enabled = true;
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            provider.ApiKey = request.ApiKey.Trim();
        }

        AiProviderResponseMapper.UpsertModel(provider, AiModelTypes.Chat, request.ChatModelName);
        AiProviderResponseMapper.UpsertModel(provider, AiModelTypes.Image, request.ImageModelName);
        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(User.GetUserId(), "save_provider", "user", id.ToString(), $"保存用户 {id} 的 API 配置", cancellationToken);
        return Ok(AiProviderResponseMapper.ToResponse(provider));
    }

    [HttpPost("users/{id:long}/provider/copy-current")]
    public async Task<ActionResult<ProviderResponse>> CopyCurrentProviderToUser(long id, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(x => x.Id == id, cancellationToken)) return NotFound(new { message = "用户不存在。" });

        var adminUserId = User.GetUserId();
        var source = await db.AiProviders
            .Include(x => x.Models)
            .Where(x => x.UserId == adminUserId && x.Enabled)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (source is null || string.IsNullOrWhiteSpace(source.BaseUrl) || string.IsNullOrWhiteSpace(source.ApiKey))
        {
            return BadRequest(new { message = "管理员账号还没有保存可用的 API 配置。" });
        }

        var target = await db.AiProviders
            .Include(x => x.Models)
            .FirstOrDefaultAsync(x => x.UserId == id && x.Name == source.Name, cancellationToken);

        if (target is null)
        {
            target = new AiProvider { UserId = id, Name = source.Name, Enabled = true };
            db.AiProviders.Add(target);
        }

        var otherProviders = await db.AiProviders
            .Where(x => x.UserId == id && x.Id != target.Id)
            .ToListAsync(cancellationToken);
        foreach (var other in otherProviders)
        {
            other.Enabled = false;
        }

        target.BaseUrl = source.BaseUrl;
        target.ApiKey = source.ApiKey;
        target.Enabled = true;
        AiProviderResponseMapper.UpsertModel(target, AiModelTypes.Chat, source.Models.FirstOrDefault(x => x.ModelType == AiModelTypes.Chat)?.Name ?? "");
        AiProviderResponseMapper.UpsertModel(target, AiModelTypes.Image, source.Models.FirstOrDefault(x => x.ModelType == AiModelTypes.Image)?.Name ?? "");
        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(User.GetUserId(), "copy_provider", "user", id.ToString(), $"为用户 {id} 引入管理员 API 配置", cancellationToken);
        return Ok(AiProviderResponseMapper.ToResponse(target));
    }

    [HttpGet("runtime-config")]
    public async Task<ActionResult<AdminRuntimeConfigDto>> RuntimeConfig()
    {
        var settings = await appSettingsService.GetAuthEmailSettingsAsync(HttpContext.RequestAborted);
        return Ok(new AdminRuntimeConfigDto(
            settings.AllowRegistration,
            settings.RequireEmailCode,
            settings.EmailCodeEnabled,
            settings.SmtpHost ?? "",
            settings.SmtpPort,
            settings.EnableSsl,
            settings.UserName ?? "",
            settings.FromEmail ?? "",
            settings.FromName ?? "内容运营助手",
            settings.CodeMinutes));
    }

    [HttpPost("runtime-config")]
    public async Task<ActionResult<AdminRuntimeConfigDto>> SaveRuntimeConfig(AdminRuntimeConfigRequest request, CancellationToken cancellationToken)
    {
        await appSettingsService.SaveAuthEmailSettingsAsync(new AuthEmailSettings(
            request.AllowRegistration,
            request.RequireEmailCode,
            request.EmailCodeEnabled,
            request.SmtpHost,
            request.SmtpPort,
            request.EnableSsl,
            request.UserName,
            request.Password,
            request.FromEmail,
            request.FromName,
            request.CodeMinutes), cancellationToken);
        await auditLogger.LogAsync(User.GetUserId(), "save_runtime_config", "settings", "auth_email", "保存注册与邮件配置", cancellationToken);
        return await RuntimeConfig();
    }

    [HttpGet("prompt-config")]
    public async Task<ActionResult<AdminPromptConfigDto>> PromptConfig(CancellationToken cancellationToken)
    {
        var settings = await appSettingsService.GetPromptSettingsAsync(cancellationToken);
        return Ok(new AdminPromptConfigDto(
            settings.Global,
            settings.Chat,
            settings.Article,
            settings.Document,
            settings.Image,
            settings.Rewrite,
            settings.Layout));
    }

    [HttpPost("prompt-config")]
    public async Task<ActionResult<AdminPromptConfigDto>> SavePromptConfig(AdminPromptConfigDto request, CancellationToken cancellationToken)
    {
        await appSettingsService.SavePromptSettingsAsync(new PromptSettings(
            request.Global,
            request.Chat,
            request.Article,
            request.Document,
            request.Image,
            request.Rewrite,
            request.Layout), cancellationToken);
        await auditLogger.LogAsync(User.GetUserId(), "save_prompt_config", "settings", "prompts", "保存提示词配置", cancellationToken);
        return await PromptConfig(cancellationToken);
    }

    private static string BuildPreview(string requestJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(requestJson);
            if (doc.RootElement.TryGetProperty("content", out var content))
            {
                var text = content.GetString() ?? "";
                var normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
            }
        }
        catch
        {
            // Ignore malformed old job payloads.
        }

        return "";
    }
}

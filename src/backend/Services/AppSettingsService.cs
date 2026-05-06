using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Data;

namespace VeraMedia.Api.Services;

public sealed record AuthEmailSettings(
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

public sealed record PromptSettings(
    string Global,
    string Chat,
    string Article,
    string Document,
    string Image,
    string Rewrite,
    string Layout);

public interface IAppSettingsService
{
    Task<AuthEmailSettings> GetAuthEmailSettingsAsync(CancellationToken cancellationToken);
    Task SaveAuthEmailSettingsAsync(AuthEmailSettings settings, CancellationToken cancellationToken);
    Task<PromptSettings> GetPromptSettingsAsync(CancellationToken cancellationToken);
    Task SavePromptSettingsAsync(PromptSettings settings, CancellationToken cancellationToken);
}

public sealed class AppSettingsService(AppDbContext db) : IAppSettingsService
{
    public async Task<AuthEmailSettings> GetAuthEmailSettingsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.AppSettings.ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);
        return new AuthEmailSettings(
            GetBool(rows, "auth.allowRegistration", true),
            GetBool(rows, "auth.requireEmailCode", false),
            GetBool(rows, "email.enabled", false),
            Get(rows, "email.smtpHost", ""),
            GetInt(rows, "email.smtpPort", 587),
            GetBool(rows, "email.enableSsl", true),
            Get(rows, "email.userName", ""),
            Get(rows, "email.password", ""),
            Get(rows, "email.fromEmail", ""),
            Get(rows, "email.fromName", "内容运营助手"),
            GetInt(rows, "email.codeMinutes", 10));
    }

    public async Task SaveAuthEmailSettingsAsync(AuthEmailSettings settings, CancellationToken cancellationToken)
    {
        await SetAsync("auth.allowRegistration", settings.AllowRegistration.ToString(), cancellationToken);
        await SetAsync("auth.requireEmailCode", settings.RequireEmailCode.ToString(), cancellationToken);
        await SetAsync("email.enabled", settings.EmailCodeEnabled.ToString(), cancellationToken);
        await SetAsync("email.smtpHost", settings.SmtpHost ?? "", cancellationToken);
        await SetAsync("email.smtpPort", settings.SmtpPort.ToString(), cancellationToken);
        await SetAsync("email.enableSsl", settings.EnableSsl.ToString(), cancellationToken);
        await SetAsync("email.userName", settings.UserName ?? "", cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.Password))
        {
            await SetAsync("email.password", settings.Password, cancellationToken);
        }
        await SetAsync("email.fromEmail", settings.FromEmail ?? "", cancellationToken);
        await SetAsync("email.fromName", settings.FromName ?? "内容运营助手", cancellationToken);
        await SetAsync("email.codeMinutes", Math.Max(1, settings.CodeMinutes).ToString(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PromptSettings> GetPromptSettingsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.AppSettings.ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);
        return new PromptSettings(
            Get(rows, "prompt.global", ""),
            Get(rows, "prompt.chat", ""),
            Get(rows, "prompt.article", ""),
            Get(rows, "prompt.document", ""),
            Get(rows, "prompt.image", ""),
            Get(rows, "prompt.rewrite", ""),
            Get(rows, "prompt.layout", ""));
    }

    public async Task SavePromptSettingsAsync(PromptSettings settings, CancellationToken cancellationToken)
    {
        await SetAsync("prompt.global", settings.Global ?? "", cancellationToken);
        await SetAsync("prompt.chat", settings.Chat ?? "", cancellationToken);
        await SetAsync("prompt.article", settings.Article ?? "", cancellationToken);
        await SetAsync("prompt.document", settings.Document ?? "", cancellationToken);
        await SetAsync("prompt.image", settings.Image ?? "", cancellationToken);
        await SetAsync("prompt.rewrite", settings.Rewrite ?? "", cancellationToken);
        await SetAsync("prompt.layout", settings.Layout ?? "", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (row is null)
        {
            db.AppSettings.Add(new() { Key = key, Value = value });
            return;
        }

        row.Value = value;
    }

    private static string Get(Dictionary<string, string> rows, string key, string fallback)
        => rows.TryGetValue(key, out var value) ? value : fallback;

    private static bool GetBool(Dictionary<string, string> rows, string key, bool fallback)
        => bool.TryParse(Get(rows, key, ""), out var value) ? value : fallback;

    private static int GetInt(Dictionary<string, string> rows, string key, int fallback)
        => int.TryParse(Get(rows, key, ""), out var value) ? value : fallback;
}

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;
using VeraMedia.Api.Services;

namespace VeraMedia.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/assets")]
public sealed partial class AssetsController(AppDbContext db, IAuditLogger auditLogger) : ControllerBase
{
    [HttpGet("articles")]
    public async Task<ActionResult<IReadOnlyList<ArticleAssetDto>>> Articles(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var latestArticleRefs = await db.Articles
            .AsNoTracking()
            .Include(x => x.Project)
            .Where(x => x.Project!.UserId == userId)
            .Select(x => new
            {
                x.Id,
                x.ProjectId,
                x.Version,
                x.CreatedAt,
                ProjectUpdatedAt = x.Project!.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        var latestArticleIds = latestArticleRefs
            .GroupBy(x => x.ProjectId)
            .Select(group => group
                .OrderByDescending(x => x.Version)
                .ThenByDescending(x => x.CreatedAt)
                .First())
            .OrderByDescending(x => x.ProjectUpdatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => x.Id)
            .ToList();

        if (latestArticleIds.Count == 0)
        {
            return Ok(Array.Empty<ArticleAssetDto>());
        }

        var articles = await db.Articles
            .AsNoTracking()
            .Include(x => x.Project)
            .Where(x => latestArticleIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        return Ok(articles
            .OrderBy(x => latestArticleIds.IndexOf(x.Id))
            .Select(ToArticleDto)
            .ToList());
    }

    [HttpGet("articles/{projectId:long}/versions")]
    public async Task<ActionResult<IReadOnlyList<ArticleVersionDto>>> ArticleVersions(long projectId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var exists = await db.ContentProjects
            .AsNoTracking()
            .AnyAsync(x => x.Id == projectId && x.UserId == userId, cancellationToken);
        if (!exists)
        {
            return NotFound(new { message = "文章资产不存在或无权访问。" });
        }

        var versions = await db.Articles
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.CreatedAt)
            .Take(30)
            .ToListAsync(cancellationToken);

        return Ok(versions.Select(ToArticleVersionDto).ToList());
    }

    [HttpPost("articles/{projectId:long}/versions/{articleId:long}/restore")]
    public async Task<ActionResult<ArticleAssetDto>> RestoreArticleVersion(long projectId, long articleId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var source = await db.Articles
            .Include(x => x.Project)
            .FirstOrDefaultAsync(x =>
                x.Id == articleId &&
                x.ProjectId == projectId &&
                x.Project!.UserId == userId,
                cancellationToken);
        if (source is null)
        {
            return NotFound(new { message = "文章版本不存在或无权恢复。" });
        }

        var currentVersion = await db.Articles
            .Where(x => x.ProjectId == projectId)
            .Select(x => (int?)x.Version)
            .MaxAsync(cancellationToken) ?? 0;

        source.Project!.Title = source.Title;
        source.Project.UpdatedAt = DateTime.UtcNow;

        var restored = new Article
        {
            ProjectId = projectId,
            Title = source.Title,
            Body = source.Body,
            Platform = source.Platform,
            Version = currentVersion + 1,
            CreatedAt = DateTime.UtcNow
        };
        db.Articles.Add(restored);

        foreach (var image in ExtractImages(source.Body))
        {
            db.GeneratedImages.Add(new GeneratedImage
            {
                ProjectId = projectId,
                Prompt = image.Alt,
                ImageUrl = image.Url,
                Status = "completed",
                CreatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(userId, "restore_article_version", "content_project", projectId.ToString(), $"恢复文章版本 {articleId}", cancellationToken);
        restored.Project = source.Project;
        return Ok(ToArticleDto(restored));
    }

    [HttpGet("images")]
    public async Task<ActionResult<IReadOnlyList<ImageAssetDto>>> Images(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var images = await db.GeneratedImages
            .AsNoTracking()
            .Include(x => x.Project)
            .Where(x => x.Project!.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(160)
            .ToListAsync(cancellationToken);

        return Ok(images.Select(x => new ImageAssetDto(
            x.Id,
            x.ProjectId,
            x.Project?.Title ?? "内容项目",
            x.Prompt,
            x.ImageUrl,
            x.Status,
            x.CreatedAt)).ToList());
    }

    [HttpDelete("articles/{projectId:long}")]
    public async Task<IActionResult> DeleteArticleAsset(long projectId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var project = await db.ContentProjects
            .FirstOrDefaultAsync(x => x.Id == projectId && x.UserId == userId, cancellationToken);
        if (project is null)
        {
            return NotFound(new { message = "文章资产不存在或无权删除。" });
        }

        var articleIds = await db.Articles
            .Where(x => x.ProjectId == projectId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var shares = await db.ArticleShares
            .Where(x => articleIds.Contains(x.ArticleId))
            .ToListAsync(cancellationToken);
        var articles = await db.Articles
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        var images = await db.GeneratedImages
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        db.ArticleShares.RemoveRange(shares);
        db.GeneratedImages.RemoveRange(images);
        db.Articles.RemoveRange(articles);
        db.ContentProjects.Remove(project);
        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(userId, "delete_article_asset", "content_project", projectId.ToString(), $"删除文章资产 {project.Title}", cancellationToken);
        return NoContent();
    }

    [HttpPost("articles/batch-delete")]
    public async Task<ActionResult<BatchDeleteArticlesResponse>> BatchDeleteArticleAssets(BatchDeleteArticlesRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var projectIds = request.ProjectIds
            .Where(id => id > 0)
            .Distinct()
            .Take(100)
            .ToArray();
        if (projectIds.Length == 0)
        {
            return BadRequest(new { message = "请选择要删除的文章资产。" });
        }

        var projects = await db.ContentProjects
            .Where(x => projectIds.Contains(x.Id) && x.UserId == userId)
            .ToListAsync(cancellationToken);
        if (projects.Count == 0)
        {
            return NotFound(new { message = "文章资产不存在或无权删除。" });
        }

        var accessibleProjectIds = projects.Select(x => x.Id).ToArray();
        var articleIds = await db.Articles
            .Where(x => accessibleProjectIds.Contains(x.ProjectId))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var shares = await db.ArticleShares
            .Where(x => articleIds.Contains(x.ArticleId))
            .ToListAsync(cancellationToken);
        var articles = await db.Articles
            .Where(x => accessibleProjectIds.Contains(x.ProjectId))
            .ToListAsync(cancellationToken);
        var images = await db.GeneratedImages
            .Where(x => accessibleProjectIds.Contains(x.ProjectId))
            .ToListAsync(cancellationToken);

        db.ArticleShares.RemoveRange(shares);
        db.GeneratedImages.RemoveRange(images);
        db.Articles.RemoveRange(articles);
        db.ContentProjects.RemoveRange(projects);
        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(userId, "batch_delete_article_assets", "content_project", string.Join(",", accessibleProjectIds), $"批量删除文章资产 {projects.Count} 个", cancellationToken);
        return Ok(new BatchDeleteArticlesResponse(projects.Count));
    }

    [HttpDelete("images/{imageId:long}")]
    public async Task<IActionResult> DeleteImageAsset(long imageId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var image = await db.GeneratedImages
            .Include(x => x.Project)
            .FirstOrDefaultAsync(x => x.Id == imageId && x.Project!.UserId == userId, cancellationToken);
        if (image is null)
        {
            return NotFound(new { message = "图片资产不存在或无权删除。" });
        }

        db.GeneratedImages.Remove(image);
        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(userId, "delete_image_asset", "generated_image", imageId.ToString(), $"删除图片资产 {image.Prompt.Clamp(80)}", cancellationToken);
        return NoContent();
    }

    [HttpPost("images/batch-delete")]
    public async Task<ActionResult<BatchDeleteImagesResponse>> BatchDeleteImageAssets(BatchDeleteImagesRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var imageIds = request.ImageIds
            .Where(id => id > 0)
            .Distinct()
            .Take(200)
            .ToArray();
        if (imageIds.Length == 0)
        {
            return BadRequest(new { message = "请选择要删除的图片。" });
        }

        var images = await db.GeneratedImages
            .Include(x => x.Project)
            .Where(x => imageIds.Contains(x.Id) && x.Project!.UserId == userId)
            .ToListAsync(cancellationToken);

        if (images.Count == 0)
        {
            return NotFound(new { message = "图片资产不存在或无权删除。" });
        }

        db.GeneratedImages.RemoveRange(images);
        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(userId, "batch_delete_image_assets", "generated_image", string.Join(",", images.Select(x => x.Id)), $"批量删除图片资产 {images.Count} 张", cancellationToken);
        return Ok(new BatchDeleteImagesResponse(images.Count));
    }
    [HttpPost("articles/from-message")]
    public async Task<ActionResult<ArticleAssetDto>> SaveFromMessage(SaveArticleAssetRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var message = await db.ConversationMessages
            .Include(x => x.Conversation)
            .FirstOrDefaultAsync(x =>
                x.Id == request.MessageId &&
                x.ConversationId == request.ConversationId &&
                x.Role == "assistant" &&
                x.Conversation!.UserId == userId,
                cancellationToken);
        if (message is null)
        {
            return NotFound(new { message = "内容不存在或无权保存。" });
        }

        var body = ArticleMarkdownImageComposer.StripImagePromptComments(
            string.IsNullOrWhiteSpace(request.Body) ? message.Content : request.Body);
        if (string.IsNullOrWhiteSpace(body))
        {
            return BadRequest(new { message = "没有可保存的正文内容。" });
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? ExtractTitle(body) : request.Title.Trim();
        var sourceUrl = BuildSourceUrl(request.ConversationId, request.MessageId);
        var project = await db.ContentProjects.FirstOrDefaultAsync(x => x.UserId == userId && x.SourceUrl == sourceUrl, cancellationToken);
        if (project is null)
        {
            project = new ContentProject
            {
                UserId = userId,
                Title = title,
                SourceUrl = sourceUrl,
                Status = "draft",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.ContentProjects.Add(project);
        }
        else
        {
            project.Title = title;
            project.UpdatedAt = DateTime.UtcNow;
        }

        var currentVersion = await db.Articles
            .Where(x => x.ProjectId == project.Id)
            .Select(x => (int?)x.Version)
            .MaxAsync(cancellationToken) ?? 0;

        var article = new Article
        {
            Project = project,
            Title = title,
            Body = body,
            Platform = "wechat",
            Version = currentVersion + 1,
            CreatedAt = DateTime.UtcNow
        };
        db.Articles.Add(article);

        foreach (var image in ExtractImages(body))
        {
            db.GeneratedImages.Add(new GeneratedImage
            {
                Project = project,
                Prompt = image.Alt,
                ImageUrl = image.Url,
                Status = "completed",
                CreatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(userId, "save_article_asset", "content_project", project.Id.ToString(), $"保存文章资产 {title}", cancellationToken);
        await db.Entry(article).Reference(x => x.Project).LoadAsync(cancellationToken);
        return Ok(ToArticleDto(article));
    }

    private static ArticleAssetDto ToArticleDto(Article article)
    {
        var (conversationId, messageId) = ParseSourceUrl(article.Project?.SourceUrl);
        return new ArticleAssetDto(
            article.Id,
            article.ProjectId,
            article.Title,
            BuildExcerpt(article.Body),
            article.Platform,
            article.Version,
            article.Project?.Status ?? "draft",
            conversationId,
            messageId,
            article.CreatedAt,
            article.Project?.UpdatedAt ?? article.CreatedAt);
    }

    private static ArticleVersionDto ToArticleVersionDto(Article article) => new(
        article.Id,
        article.ProjectId,
        article.Title,
        BuildExcerpt(article.Body),
        article.Body,
        article.Platform,
        article.Version,
        article.CreatedAt);

    private static string BuildSourceUrl(long conversationId, long messageId) => $"conversation:{conversationId}:message:{messageId}";

    private static (long? ConversationId, long? MessageId) ParseSourceUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        var match = SourceUrlRegex().Match(value);
        return match.Success
            ? (long.Parse(match.Groups[1].Value), long.Parse(match.Groups[2].Value))
            : (null, null);
    }

    private static string ExtractTitle(string body)
    {
        var line = body.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "未命名文章";
        return line.Replace("#", "").Replace("*", "").Trim().Trim('，', '。', '：', ':').Clamp(80);
    }

    private static string BuildExcerpt(string body)
    {
        var clean = ReferenceSectionRegex().Replace(body, "");
        clean = ImageMarkdownRegex().Replace(clean, "");
        clean = MarkdownLinkRegex().Replace(clean, "$1");
        clean = RawUrlRegex().Replace(clean, "")
            .Replace("[", "")
            .Replace("]", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace("#", "")
            .Replace("*", "")
            .Replace("`", "");
        var normalized = string.Join(" ", clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Clamp(140);
    }

    private static IEnumerable<(string Alt, string Url)> ExtractImages(string body)
    {
        foreach (Match match in ImageMarkdownRegex().Matches(body))
        {
            yield return (match.Groups[1].Value.Clamp(200), match.Groups[2].Value);
        }
    }

    [GeneratedRegex(@"conversation:(\d+):message:(\d+)")]
    private static partial Regex SourceUrlRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex ImageMarkdownRegex();

    [GeneratedRegex(@"(?:\r?\n){2,}(?:---\s*(?:\r?\n){1,3})?(?:#{1,6}\s*)?(?:参考资料|参考来源|引用来源|资料来源|参考文献|参考链接|来源链接|References|Sources)\s*(?:[:：]|\r?\n)[\s\S]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceSectionRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex RawUrlRegex();
}

file static class AssetStringExtensions
{
    public static string Clamp(this string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}

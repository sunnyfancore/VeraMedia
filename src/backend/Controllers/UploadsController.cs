using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeraMedia.Api.Contracts;

namespace VeraMedia.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/uploads")]
public sealed class UploadsController(IWebHostEnvironment environment) : ControllerBase
{
    private const long MaxFileSize = 10 * 1024 * 1024;
    private const long MaxTotalSize = 30 * 1024 * 1024;
    private const int MaxFileCount = 8;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".ppt", ".pptx",
        ".txt", ".md", ".json",
        ".js", ".jsx", ".ts", ".tsx", ".py", ".cs", ".java", ".go", ".rs", ".c", ".cpp", ".h", ".sql", ".html", ".css"
    };

    [HttpPost]
    [RequestSizeLimit(MaxTotalSize)]
    public async Task<ActionResult<IReadOnlyList<FileUploadResponse>>> Upload(IFormFileCollection files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return BadRequest(new { message = "请选择要上传的文件。" });
        }

        if (files.Count > MaxFileCount)
        {
            return BadRequest(new { message = $"最多上传 {MaxFileCount} 个附件。" });
        }

        if (files.Sum(file => file.Length) > MaxTotalSize)
        {
            return BadRequest(new { message = "附件总大小不能超过 30MB。" });
        }

        var uploadRoot = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "uploads");
        Directory.CreateDirectory(uploadRoot);

        var results = new List<FileUploadResponse>();
        foreach (var file in files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            var extension = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(extension))
            {
                return BadRequest(new { message = $"不支持的附件格式：{extension}" });
            }

            if (file.Length > MaxFileSize)
            {
                return BadRequest(new { message = "单个附件不能超过 10MB。" });
            }

            var safeName = $"{Guid.NewGuid():N}{extension}";
            var fullPath = Path.Combine(uploadRoot, safeName);
            await using (var stream = System.IO.File.Create(fullPath))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            var url = $"{Request.Scheme}://{Request.Host}/uploads/{safeName}";
            results.Add(new FileUploadResponse(
                file.FileName,
                file.ContentType,
                file.Length,
                url));
        }

        return Ok(results);
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeraMedia.Api.Contracts;

namespace VeraMedia.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/uploads")]
public sealed class UploadsController(IWebHostEnvironment environment) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(30_000_000)]
    public async Task<ActionResult<IReadOnlyList<FileUploadResponse>>> Upload(IFormFileCollection files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return BadRequest(new { message = "请选择要上传的文件。" });
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
            var safeName = $"{Guid.NewGuid():N}{extension}";
            var fullPath = Path.Combine(uploadRoot, safeName);
            await using (var stream = System.IO.File.Create(fullPath))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            results.Add(new FileUploadResponse(
                file.FileName,
                file.ContentType,
                file.Length,
                $"/uploads/{safeName}"));
        }

        return Ok(results);
    }
}

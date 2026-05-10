using System.Text;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;
using VeraMedia.Api.Services;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace VeraMedia.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/conversations")]
public sealed class ConversationsController(
    AppDbContext db,
    IConversationService conversationService,
    IConversationIntentRouter intentRouter,
    IAiProviderResolver providerResolver,
    IAiChatClient chatClient,
    IImageGenerationService imageGenerationService,
    IWebPageContentService webPageContentService,
    IAttachmentContentService attachmentContentService,
    IGenerationJobRunner generationJobRunner,
    IGenerationJobQueue generationJobQueue,
    IOfficeExportService officeExportService,
    IPptVideoConversionService pptVideoConversionService,
    IPptVideoTaskManager pptVideoTaskManager,
    IPptVideoPreviewStore pptVideoPreviewStore,
    IPptxThumbnailRenderer pptxThumbnailRenderer,
    ILibreOfficeService libreOfficeService,
    EdgeTtsClient edgeTtsClient,
    IAuditLogger auditLogger,
    ILogger<ConversationsController> logger) : ControllerBase
{
    private const long MaxPptVideoUploadSize = 100 * 1024 * 1024;
    private sealed record PptVideoPreviewSlide(int Index, string Title, string Notes);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConversationSummary>>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 80, CancellationToken cancellationToken = default)
    {
        return Ok(await conversationService.ListAsync(User.GetUserId(), cancellationToken, page, pageSize));
    }

    [HttpGet("{conversationId:long}/messages")]
    public async Task<ActionResult<IReadOnlyList<MessageDto>>> Messages(long conversationId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await conversationService.MessagesAsync(User.GetUserId(), conversationId, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpGet("{conversationId:long}/messages/{messageId:long}/export")]
    public async Task<IActionResult> ExportMessage(long conversationId, long messageId, [FromQuery] string format = "docx", CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var message = await db.ConversationMessages
            .AsNoTracking()
            .Include(x => x.Conversation)
            .FirstOrDefaultAsync(x => x.Id == messageId && x.ConversationId == conversationId && x.Conversation!.UserId == userId, cancellationToken);
        if (message is null)
        {
            return NotFound(new { message = "消息不存在或无权导出。" });
        }

        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return BadRequest(new { message = "这条消息还没有可导出的内容。" });
        }

        var title = message.Conversation?.Title ?? "VeraMedia";
        if (string.Equals(format, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            var pptContent = await EnsureAiPptSpecForExportAsync(userId, title, message.Content, cancellationToken);
            var video = await CreatePptVideoAsync(title, pptContent, cancellationToken);
            return File(video.Content, video.ContentType, video.FileName);
        }

        var file = string.Equals(format, "pptx", StringComparison.OrdinalIgnoreCase)
            ? await officeExportService.CreatePptxAsync(title, await EnsureAiPptSpecForExportAsync(userId, title, message.Content, cancellationToken))
            : officeExportService.CreateDocx(title, message.Content);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpPost("{conversationId:long}/messages/{messageId:long}/export-video")]
    public async Task<IActionResult> ExportMessageVideo(long conversationId, long messageId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var message = await db.ConversationMessages
            .AsNoTracking()
            .Include(x => x.Conversation)
            .FirstOrDefaultAsync(x => x.Id == messageId && x.ConversationId == conversationId && x.Conversation!.UserId == userId, cancellationToken);
        if (message is null)
            return NotFound(new { message = "消息不存在或无权导出。" });

        if (string.IsNullOrWhiteSpace(message.Content))
            return BadRequest(new { message = "这条消息还没有可导出的内容。" });

        var title = message.Conversation?.Title ?? "VeraMedia";
        var pptContent = await EnsureAiPptSpecForExportAsync(userId, title, message.Content, cancellationToken);

        var workDir = Path.Combine(Path.GetTempPath(), "veramedia-ppt-video-msg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var task = pptVideoTaskManager.Create(workDir);
        var fileName = BuildExportFileName(title, "mp4");

        _ = Task.Run(async () =>
        {
            try
            {
                pptVideoTaskManager.UpdateProgress(task.Id, "生成 PPTX", 10, 0, 0);
                var pptx = await officeExportService.CreatePptxAsync(title, pptContent);
                var pptxPath = Path.Combine(workDir, pptx.FileName);
                await System.IO.File.WriteAllBytesAsync(pptxPath, pptx.Content, CancellationToken.None);

                var outputDir = Path.Combine(workDir, "video");
                var request = new PptVideoConversionRequest(pptxPath, outputDir, 5, "zh", "1.0", "720p", null, 30, null);
                var result = await pptVideoConversionService.ConvertAsync(request,
                    (stage, progress, slideIdx, slideTotal) =>
                        pptVideoTaskManager.UpdateProgress(task.Id, stage, progress, slideIdx, slideTotal),
                    CancellationToken.None,
                    encoderInfo => pptVideoTaskManager.SetEncoder(task.Id, encoderInfo));
                pptVideoTaskManager.Complete(task.Id, result.VideoPath, fileName, result.EncoderInfo);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Message video export task {TaskId} failed", task.Id);
                pptVideoTaskManager.Fail(task.Id, ex.Message);
            }
        }, CancellationToken.None);

        return Ok(new { taskId = task.Id });
    }

    private async Task<string> EnsureAiPptSpecForExportAsync(long userId, string title, string content, CancellationToken cancellationToken)
    {
        if (ContainsPptSpec(content))
        {
            return content;
        }

        var provider = await providerResolver.GetActiveProviderAsync(userId, cancellationToken);
        var chatModel = providerResolver.GetEnabledModel(provider, AiModelTypes.Chat);
        if (provider is null || chatModel is null || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            return content;
        }

        var turns = new[]
        {
            new ChatTurn("system", string.Join(Environment.NewLine, [
                "你是资深 PPT 策划与视觉设计师。你的任务不是总结文章，而是把内容制作成可直接导出的成品级 PPT 规格。",
                "请只输出一个 fenced code block，语言标记必须是 ppt-spec，里面是严格 JSON。",
                "JSON 格式：{\"title\":\"整套PPT标题\",\"subtitle\":\"副标题\",\"audience\":\"受众\",\"theme\":\"商务精美/科技蓝/极简高级/发布会风/数据报告/培训课件\",\"design\":{\"style\":\"视觉风格\",\"palette\":\"色彩建议\",\"motif\":\"贯穿全稿的视觉母题\"},\"slides\":[{\"title\":\"页标题\",\"subtitle\":\"可选副标题\",\"layout\":\"cover/agenda/section/title-content/two-column/data-card/process/timeline/summary\",\"bullets\":[\"短要点\"],\"visual\":\"这一页具体如何画：图表、卡片、流程、对比矩阵、场景图或视觉隐喻\",\"notes\":\"可直接放入备注区的演讲稿\"}]}",
                "制作要求：封面和目录必须有；中间页面要交替使用不同版式；每页 bullets 控制在 2-5 条；visual 必须具体，不能写“配图即可”；notes 要与页面内容同步，方便 PPT 转视频配音。",
                "不要输出 Markdown 大纲、不要解释、不要把 JSON 拆散。"
            ])),
            new ChatTurn("user", $"请将以下内容制作成精美 PPT 规格。标题参考：{title}\n\n{content}")
        };
        var options = new AgentOptionsDto(
            "expert",
            "business",
            "pptx",
            0.35m,
            0,
            false,
            false,
            Capability: "ppt",
            CapabilityParams: new Dictionary<string, string>
            {
                ["pptMode"] = "PPT",
                ["pptDesign"] = "商务精美",
                ["pptAudience"] = "商务汇报"
            });

        var builder = new StringBuilder();
        await foreach (var chunk in chatClient.StreamReplyAsync(provider, chatModel, turns, options, cancellationToken))
        {
            builder.Append(chunk);
            if (builder.Length > 60000)
            {
                break;
            }
        }

        var generated = builder.ToString();
        return ContainsPptSpec(generated) ? generated : content;
    }

    private static bool ContainsPptSpec(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        return Regex.IsMatch(content, @"```(?:json|ppt-spec|veramedia-ppt)?[^\n`]*\n?[\s\S]*?""slides""[\s\S]*?```", RegexOptions.IgnoreCase)
            || Regex.IsMatch(content, @"PPT_SPEC\s*[:：]\s*\{[\s\S]*?""slides""[\s\S]*?\}", RegexOptions.IgnoreCase);
    }

    [HttpPost("ppt-video")]
    [RequestSizeLimit(MaxPptVideoUploadSize)]
    public async Task<IActionResult> ConvertUploadedPptToVideo(
        [FromForm] IFormFile file,
        [FromForm] int secondsPerSlide = 5,
        [FromForm] string voice = "zh",
        [FromForm] string speed = "1.0",
        [FromForm] string resolution = "720p",
        [FromForm] int volume = 30,
        [FromForm] string? notesJson = null,
        [FromForm] string? previewId = null,
        [FromForm] IFormFile? bgm = null,
        CancellationToken cancellationToken = default)
    {
        if (file.Length == 0)
            return BadRequest(new { message = "请选择 PPT 文件。" });

        if (file.Length > MaxPptVideoUploadSize)
            return BadRequest(new { message = "PPT 文件不能超过 100MB。" });

        var extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".pptx", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".ppt", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "仅支持 .ppt 和 .pptx 文件。" });

        var workDir = Path.Combine(Path.GetTempPath(), "veramedia-upload-ppt-video", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var safeInputName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            var inputPath = Path.Combine(workDir, safeInputName);
            await using (var stream = System.IO.File.Create(inputPath))
                await file.CopyToAsync(stream, cancellationToken);

            var pptxPath = await EnsurePptxForVideoAsync(inputPath, workDir, cancellationToken);
            if (!string.IsNullOrWhiteSpace(notesJson))
                ApplyPptVideoNotes(pptxPath, notesJson);

            var bgmPath = "";
            if (bgm is { Length: > 0 })
            {
                var bgmExtension = Path.GetExtension(bgm.FileName).ToLowerInvariant();
                if (!new[] { ".mp3", ".wav", ".m4a", ".aac", ".ogg" }.Contains(bgmExtension))
                    return BadRequest(new { message = "BGM 仅支持 mp3、wav、m4a、aac、ogg。" });
                bgmPath = Path.Combine(workDir, $"bgm{bgmExtension}");
                await using var bgmStream = System.IO.File.Create(bgmPath);
                await bgm.CopyToAsync(bgmStream, cancellationToken);
            }

            var outputDir = Path.Combine(workDir, "video");
            Dictionary<int, string>? overrideNotes = null;
            if (!string.IsNullOrWhiteSpace(notesJson))
                overrideNotes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, string>>(notesJson);

            var task = pptVideoTaskManager.Create(workDir);

            var fileName = BuildExportFileName(Path.GetFileNameWithoutExtension(file.FileName), "mp4");

            // Start conversion in background
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(previewId))
                        pptVideoTaskManager.UpdateProgress(task.Id, "等待高清预览帧", 12, 0, 0);
                    var renderedSlidesDir = await WaitForHqPreviewSlidesAsync(previewId, TimeSpan.FromSeconds(45), CancellationToken.None);
                    var request = new PptVideoConversionRequest(
                        pptxPath, outputDir,
                        Math.Clamp(secondsPerSlide, 2, 30),
                        string.IsNullOrWhiteSpace(voice) ? "zh" : voice.Trim(),
                        string.IsNullOrWhiteSpace(speed) ? "1.0" : speed.Trim(),
                        string.IsNullOrWhiteSpace(resolution) ? "720p" : resolution.Trim(),
                        string.IsNullOrWhiteSpace(bgmPath) ? null : bgmPath,
                        Math.Clamp(volume, 0, 100),
                        overrideNotes,
                        renderedSlidesDir);

                    var result = await pptVideoConversionService.ConvertAsync(request,
                        (stage, progress, slideIdx, slideTotal) =>
                            pptVideoTaskManager.UpdateProgress(task.Id, stage, progress, slideIdx, slideTotal),
                        CancellationToken.None,
                        encoderInfo => pptVideoTaskManager.SetEncoder(task.Id, encoderInfo));
                    pptVideoTaskManager.Complete(task.Id, result.VideoPath, fileName, result.EncoderInfo);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "PPT video conversion task {TaskId} failed", task.Id);
                    pptVideoTaskManager.Fail(task.Id, ex.Message);
                }
            }, CancellationToken.None);

            return Ok(new { taskId = task.Id });
        }
        catch (Exception ex)
        {
            return BadRequest(BuildPptVideoErrorResponse(ex));
        }
    }

    [HttpGet("ppt-video/{taskId}/status")]
    public IActionResult GetPptVideoStatus(string taskId)
    {
        var task = pptVideoTaskManager.Get(taskId);
        if (task is null)
            return NotFound(new { message = "任务不存在或已过期。" });

        return Ok(new
        {
            taskId = task.Id,
            status = task.Status,
            stage = task.Stage,
            progress = task.Progress,
            slideIndex = task.SlideIndex,
            slideTotal = task.SlideTotal,
            error = task.Error,
            fileName = task.VideoFileName,
            videoEncoder = task.VideoEncoder,
            videoEncoderMode = task.VideoEncoderMode,
            videoEncoderLabel = task.VideoEncoderLabel,
            videoEncoderDevice = task.VideoEncoderDevice,
            previewId = task.PreviewId,
            previewSlides = task.PreviewSlides,
        });
    }

    [HttpGet("ppt-video/{taskId}/download")]
    public async Task<IActionResult> DownloadPptVideo(string taskId, CancellationToken cancellationToken)
    {
        var task = pptVideoTaskManager.Get(taskId);
        if (task is null)
            return NotFound(new { message = "任务不存在或已过期。" });

        if (task.Status != "completed")
            return BadRequest(new { message = "视频尚未生成完成。" });

        if (string.IsNullOrWhiteSpace(task.VideoPath) || !System.IO.File.Exists(task.VideoPath))
            return NotFound(new { message = "视频文件不存在。" });

        var bytes = await System.IO.File.ReadAllBytesAsync(task.VideoPath, cancellationToken);
        pptVideoTaskManager.Remove(taskId);
        return File(bytes, "video/mp4", task.VideoFileName ?? "video.mp4");
    }

    [HttpPost("ppt-video/preview")]
    [HttpPost("ppt-video-preview")]
    [RequestSizeLimit(MaxPptVideoUploadSize)]
    public async Task<IActionResult> PreviewUploadedPptForVideo([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
            return BadRequest(new { message = "请选择 PPT 文件。" });

        if (file.Length > MaxPptVideoUploadSize)
            return BadRequest(new { message = "PPT 文件不能超过 100MB。" });

        var extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".pptx", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".ppt", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "仅支持 .ppt 和 .pptx 文件。" });

        var tempDir = Path.Combine(Path.GetTempPath(), "veramedia-ppt-video-preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
        await using (var stream = System.IO.File.Create(inputPath))
            await file.CopyToAsync(stream, cancellationToken);

        var task = pptVideoTaskManager.Create(tempDir);
        var bgInputPath = inputPath;

        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                pptVideoTaskManager.UpdateProgress(task.Id, "读取备注", 5, 0, 0);
                var pptxPath = string.Equals(extension, ".pptx", StringComparison.OrdinalIgnoreCase)
                    ? bgInputPath
                    : await EnsurePptxForVideoAsync(bgInputPath, tempDir, CancellationToken.None);

                var slides = ReadPptVideoSlides(pptxPath);
                var notesElapsed = sw.Elapsed;
                pptVideoTaskManager.UpdateProgress(task.Id, "渲染幻灯片画面", 20, 0, slides.Count);

                var slidesDir = Path.Combine(tempDir, "slides");
                Directory.CreateDirectory(slidesDir);
                bool hqRendered = false;
                try
                {
                    var hqWorkDir = Path.Combine(tempDir, "hq");
                    Directory.CreateDirectory(hqWorkDir);
                    var pdfPath = await libreOfficeService.ConvertToPdfAsync(pptxPath, hqWorkDir, CancellationToken.None);
                    PptVideoConversionService.RenderSlides(pdfPath, tempDir, 150);
                    hqRendered = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "HQ render failed, falling back to direct rendering");
                    try
                    {
                        var thumbnails = pptxThumbnailRenderer.RenderAllSlides(pptxPath, 1920, 1080);
                        foreach (var thumb in thumbnails)
                        {
                            var thumbPath = Path.Combine(slidesDir, $"slide-{thumb.Index:D3}.png");
                            await System.IO.File.WriteAllBytesAsync(thumbPath, thumb.PngData, CancellationToken.None);
                        }
                    }
                    catch (Exception fallbackEx)
                    {
                        logger.LogWarning(fallbackEx, "Fallback rendering also failed");
                    }
                }

                string? previewId = null;
                if (Directory.GetFiles(slidesDir, "slide-*.png").Length > 0)
                {
                    previewId = pptVideoPreviewStore.Save(tempDir);
                    if (hqRendered) pptVideoPreviewStore.MarkHqReady(previewId);
                }

                sw.Stop();
                var slideData = slides.Select(s => new PptVideoPreviewSlideData(s.Index, s.Title, s.Notes)).ToList();
                pptVideoTaskManager.CompletePreview(task.Id, file.FileName, file.Length, previewId, slideData);
                logger.LogInformation("PPT preview complete: {Slides} slides in {Elapsed}", slides.Count, sw.Elapsed);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PPT preview task {TaskId} failed", task.Id);
                pptVideoTaskManager.Fail(task.Id, ex.Message);
            }
        }, CancellationToken.None);

        return Ok(new { taskId = task.Id });
    }

    [HttpGet("ppt-video/preview/{previewId}/slide/{index:int}")]
    [AllowAnonymous]
    public IActionResult GetPptVideoSlideImage(string previewId, int index)
    {
        var slidePath = pptVideoPreviewStore.GetSlidePath(previewId, index);
        if (slidePath is null)
            return NotFound(new { message = "幻灯片图片不存在或已过期。" });

        return PhysicalFile(slidePath, "image/png");
    }

    private async Task<string?> WaitForHqPreviewSlidesAsync(string? previewId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(previewId))
            return null;

        var slidesDir = pptVideoPreviewStore.GetSlidesDirectory(previewId);
        if (!string.IsNullOrWhiteSpace(slidesDir))
        {
            if (pptVideoPreviewStore.IsHqReady(previewId))
                return slidesDir;

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                if (pptVideoPreviewStore.IsHqReady(previewId))
                    return pptVideoPreviewStore.GetSlidesDirectory(previewId);
                await Task.Delay(500, cancellationToken);
            }

            return pptVideoPreviewStore.GetSlidesDirectory(previewId);
        }

        var waitDeadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < waitDeadline && !cancellationToken.IsCancellationRequested)
        {
            slidesDir = pptVideoPreviewStore.GetSlidesDirectory(previewId);
            if (!string.IsNullOrWhiteSpace(slidesDir))
                return slidesDir;
            await Task.Delay(1000, cancellationToken);
        }

        logger.LogInformation("Preview slides not available for {PreviewId}; conversion will render slides itself", previewId);
        return null;
    }

    private static readonly Dictionary<string, string> VoiceSampleTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "你好，这是语音试听。欢迎使用 PPT 视频转换功能。",
        ["zh-f"] = "你好，这是语音试听。欢迎使用 PPT 视频转换功能。",
        ["zh-m"] = "你好，这是语音试听。欢迎使用 PPT 视频转换功能。",
        ["zh-news"] = "大家好，欢迎收看本期节目。今天我们来聊一聊这个话题。",
        ["zh-story"] = "很久很久以前，在一个美丽的小村庄里，住着一位善良的老人。",
        ["zh-gentle"] = "你好，这是语音试听。欢迎使用 PPT 视频转换功能。",
        ["zh-cheerful"] = "你好，这是语音试听。欢迎使用 PPT 视频转换功能。",
        ["zh-boy"] = "你好，这是语音试听。欢迎使用 PPT 视频转换功能。",
        ["zh-senior"] = "你好，这是语音试听。欢迎使用 PPT 视频转换功能。",
        ["en"] = "Hello, this is a voice sample. Welcome to the PPT video conversion feature.",
        ["en-f"] = "Hello, this is a voice sample. Welcome to the PPT video conversion feature.",
        ["en-m"] = "Hello, this is a voice sample. Welcome to the PPT video conversion feature.",
        ["en-aria"] = "Hello, this is a voice sample. Welcome to the PPT video conversion feature.",
        ["en-davis"] = "Hello, this is a voice sample. Welcome to the PPT video conversion feature.",
        ["en-gb"] = "Hello, this is a voice sample. Welcome to the PPT video conversion feature.",
        ["en-gb-m"] = "Hello, this is a voice sample. Welcome to the PPT video conversion feature.",
        ["ja"] = "こんにちは、これは音声サンプルです。PPT 動画変換機能へようこそ。",
        ["ja-m"] = "こんにちは、これは音声サンプルです。PPT 動画変換機能へようこそ。",
        ["ko"] = "안녕하세요, 이것은 음성 샘플입니다. PPT 동영상 변환 기능에 오신 것을 환영합니다.",
        ["ko-m"] = "안녕하세요, 이것은 음성 샘플입니다. PPT 동영상 변환 기능에 오신 것을 환영합니다.",
        ["melo-zh"] = "你好，这是语音试听。欢迎使用 PPT 视频转换功能。",
        ["melo-en"] = "Hello, this is a voice sample. Welcome to the PPT video conversion feature.",
        ["melo-ja"] = "こんにちは、これは音声サンプルです。PPT 動画変換機能へようこそ。",
        ["melo-ko"] = "안녕하세요, 이것은 음성 샘플입니다. PPT 동영상 변환 기능에 오신 것을 환영합니다.",
    };

    private static readonly Dictionary<string, string> VoiceMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "zh-CN-XiaoxiaoNeural",
        ["zh-f"] = "zh-CN-XiaoxiaoNeural",
        ["zh-m"] = "zh-CN-YunxiNeural",
        ["zh-news"] = "zh-CN-YunyangNeural",
        ["zh-story"] = "zh-CN-XiaoyiNeural",
        ["zh-gentle"] = "zh-CN-XiaochenNeural",
        ["zh-cheerful"] = "zh-CN-XiaoxuanNeural",
        ["zh-boy"] = "zh-CN-YunfengNeural",
        ["zh-senior"] = "zh-CN-YunjianNeural",
        ["en"] = "en-US-JennyNeural",
        ["en-f"] = "en-US-JennyNeural",
        ["en-m"] = "en-US-GuyNeural",
        ["en-aria"] = "en-US-AriaNeural",
        ["en-davis"] = "en-US-DavisNeural",
        ["en-gb"] = "en-GB-SoniaNeural",
        ["en-gb-m"] = "en-GB-RyanNeural",
        ["ja"] = "ja-JP-NanamiNeural",
        ["ja-m"] = "ja-JP-KeitaNeural",
        ["ko"] = "ko-KR-SunHiNeural",
        ["ko-m"] = "ko-KR-InJoonNeural",
        ["melo-zh"] = "zh-CN-XiaoxiaoNeural",
        ["melo-en"] = "en-US-JennyNeural",
        ["melo-ja"] = "ja-JP-NanamiNeural",
        ["melo-ko"] = "ko-KR-SunHiNeural",
    };

    [HttpGet("ppt-video/voice-sample")]
    [AllowAnonymous]
    public async Task<IActionResult> GetVoiceSample([FromQuery] string voice, [FromQuery] string? speed, CancellationToken cancellationToken)
    {
        var voiceKey = string.IsNullOrWhiteSpace(voice) ? "zh" : voice.Trim();
        var rate = string.IsNullOrWhiteSpace(speed) ? "+0%" : speed.Trim();
        var sampleText = VoiceSampleTexts.GetValueOrDefault(voiceKey, VoiceSampleTexts["zh"]);
        var edgeVoice = VoiceMap.GetValueOrDefault(voiceKey, "zh-CN-XiaoxiaoNeural");

        try
        {
            var audio = await edgeTtsClient.SynthesizeAsync(sampleText, edgeVoice, rate, cancellationToken);
            return File(audio, "audio/mpeg", "sample.mp3");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Edge TTS sample failed for voice {Voice}", voiceKey);
            return StatusCode(503, new { message = "语音服务暂不可用，请稍后重试。" });
        }
    }

    private async Task<OfficeExportFile> CreatePptVideoAsync(string title, string markdown, CancellationToken cancellationToken)
    {
        var pptx = await officeExportService.CreatePptxAsync(title, markdown);
        var workDir = Path.Combine(Path.GetTempPath(), "veramedia-ppt-video", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var pptxPath = Path.Combine(workDir, pptx.FileName);
            await System.IO.File.WriteAllBytesAsync(pptxPath, pptx.Content, cancellationToken);

            var outputDir = Path.Combine(workDir, "video");
            var request = new PptVideoConversionRequest(pptxPath, outputDir, 5, "zh", "1.0", "720p", null, 30, null);
            var result = await pptVideoConversionService.ConvertAsync(request, null, cancellationToken);
            var bytes = await System.IO.File.ReadAllBytesAsync(result.VideoPath, cancellationToken);
            return new OfficeExportFile(BuildExportFileName(title, "mp4"), "video/mp4", bytes);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static async Task<string> EnsurePptxForVideoAsync(string inputPath, string workDir, CancellationToken cancellationToken)
    {
        if (string.Equals(Path.GetExtension(inputPath), ".pptx", StringComparison.OrdinalIgnoreCase))
        {
            return inputPath;
        }

        var libreOffice = ResolveLibreOffice();
        if (libreOffice is null)
        {
            throw new InvalidOperationException("当前环境不支持 .ppt 文件转换，请上传 .pptx 格式的文件。");
        }

        var convertedDir = Path.Combine(workDir, "pptx");
        Directory.CreateDirectory(convertedDir);
        var startInfo = new ProcessStartInfo
        {
            FileName = libreOffice,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add("pptx");
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(convertedDir);
        startInfo.ArgumentList.Add(inputPath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动文件转换工具。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = (await stderrTask).Trim();
        _ = await stdoutTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "PPT 转 PPTX 失败。" : "PPT 转 PPTX 失败。");
        }

        var pptxPath = Directory.GetFiles(convertedDir, "*.pptx").FirstOrDefault();
        return !string.IsNullOrWhiteSpace(pptxPath) && System.IO.File.Exists(pptxPath)
            ? pptxPath
            : throw new InvalidOperationException("PPT 转 PPTX 完成，但没有找到转换后的文件。");
    }

    private object BuildPptVideoErrorResponse(Exception ex)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        logger.LogError(ex, "PPT video conversion failed. RequestId={RequestId}", requestId);
        return new
        {
            message = string.IsNullOrWhiteSpace(ex.Message)
                ? $"PPT 转视频失败：服务没有返回具体错误。请在 docker logs 中搜索诊断编号 {requestId}。"
                : $"{ex.Message}\n诊断编号：{requestId}",
            requestId,
        };
    }

    private static IReadOnlyList<PptVideoPreviewSlide> ReadPptVideoSlides(string pptxPath)
    {
        try
        {
            using var doc = PresentationDocument.Open(pptxPath, false);
            var slideParts = GetOrderedSlideParts(doc).ToList();
            var slides = new List<PptVideoPreviewSlide>();
            for (var i = 0; i < slideParts.Count; i++)
            {
                var slidePart = slideParts[i];
                var texts = slidePart.Slide?.Descendants<A.Text>()
                    .Select(x => x.Text?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .ToList() ?? [];
                var notes = string.Join("\n", slidePart.NotesSlidePart?.NotesSlide?.Descendants<A.Text>()
                    .Select(x => x.Text?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x) && !PptVideoConversionService.IsNoise(x)) ?? []);
                slides.Add(new PptVideoPreviewSlide(i + 1, texts.FirstOrDefault() ?? $"第 {i + 1} 页", notes.Trim()));
            }

            if (slides.Count > 0)
            {
                return slides;
            }
        }
        catch
        {
            // Fall through to a tolerant ZIP-based preview reader.
        }

        return ReadPptVideoSlidesFromZip(pptxPath);
    }

    private static IReadOnlyList<PptVideoPreviewSlide> ReadPptVideoSlidesFromZip(string pptxPath)
    {
        using var archive = ZipFile.OpenRead(pptxPath);
        var slideEntries = archive.Entries
            .Where(x => Regex.IsMatch(x.FullName, @"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase))
            .OrderBy(x => ExtractTrailingNumber(x.FullName))
            .ToList();

        var result = new List<PptVideoPreviewSlide>();
        for (var i = 0; i < slideEntries.Count; i++)
        {
            var slideText = ReadZipEntryText(slideEntries[i]);
            var title = ExtractOpenXmlTexts(slideText).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? $"第 {i + 1} 页";
            var notesEntry = archive.GetEntry($"ppt/notesSlides/notesSlide{i + 1}.xml");
            var notes = notesEntry is null ? "" : string.Join("\n", ExtractOpenXmlTexts(ReadZipEntryText(notesEntry)).Where(x => !PptVideoConversionService.IsNoise(x)));
            result.Add(new PptVideoPreviewSlide(i + 1, title, notes.Trim()));
        }

        if (result.Count == 0)
        {
            result.Add(new PptVideoPreviewSlide(1, "PPT 已上传", ""));
        }

        return result;
    }

    private static string ReadZipEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<string> ExtractOpenXmlTexts(string xml)
    {
        return Regex.Matches(xml, @"<a:t[^>]*>(.*?)</a:t>", RegexOptions.Singleline)
            .Select(x => System.Net.WebUtility.HtmlDecode(x.Groups[1].Value).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static int ExtractTrailingNumber(string value)
    {
        var match = Regex.Match(value, @"(\d+)(?=\.xml$)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : int.MaxValue;
    }

    private static void ApplyPptVideoNotes(string pptxPath, string notesJson)
    {
        var notesBySlide = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, string>>(notesJson);
        if (notesBySlide is null || notesBySlide.Count == 0) return;

        using var doc = PresentationDocument.Open(pptxPath, true);
        var slideParts = GetOrderedSlideParts(doc).ToList();
        for (var i = 0; i < slideParts.Count; i++)
        {
            if (!notesBySlide.TryGetValue(i + 1, out var notes)) continue;
            var slidePart = slideParts[i];
            var notesPart = slidePart.NotesSlidePart ?? slidePart.AddNewPart<NotesSlidePart>();
            var text = notesPart.NotesSlide?.Descendants<A.Text>().FirstOrDefault();
            if (text is not null)
            {
                text.Text = notes ?? "";
                foreach (var extra in notesPart.NotesSlide!.Descendants<A.Text>().Skip(1))
                {
                    extra.Text = "";
                }
            }
            else
            {
                notesPart.NotesSlide = CreateNotesSlide(notes ?? "");
            }

            notesPart.NotesSlide!.Save();
        }
    }

    private static IEnumerable<SlidePart> GetOrderedSlideParts(PresentationDocument doc)
    {
        var presentationPart = doc.PresentationPart;
        var slideIds = presentationPart?.Presentation?.SlideIdList?.Elements<P.SlideId>() ?? [];
        foreach (var slideId in slideIds)
        {
            var relationshipId = slideId.RelationshipId?.Value;
            if (string.IsNullOrWhiteSpace(relationshipId)) continue;
            if (presentationPart?.GetPartById(relationshipId) is SlidePart slidePart)
            {
                yield return slidePart;
            }
        }
    }

    private static P.NotesSlide CreateNotesSlide(string notes)
    {
        var shapeTree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));

        shapeTree.Append(new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = 2, Name = "Notes" },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = 680000, Y = 700000 }, new A.Extents { Cx = 11200000, Cy = 5200000 }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            new P.TextBody(
                new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
                new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.Text(notes ?? ""))))));
        return new P.NotesSlide(new P.CommonSlideData(shapeTree), new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static string? ResolveLibreOffice()
    {
        foreach (var name in new[] { "libreoffice", "soffice" })
        {
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in paths)
            {
                var fullPath = Path.Combine(dir, OperatingSystem.IsWindows() ? $"{name}.exe" : name);
                if (System.IO.File.Exists(fullPath)) return fullPath;
            }
        }

        return null;
    }

    private static string BuildExportFileName(string title, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "VeraMedia";
        return $"{cleaned[..Math.Min(cleaned.Length, 32)]}.{extension}";
    }

    [HttpDelete("{conversationId:long}")]
    public async Task<IActionResult> Delete(long conversationId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == userId, cancellationToken);
        if (conversation is null)
        {
            return NotFound(new { message = "会话不存在。" });
        }

        var jobs = await db.GenerationJobs
            .Where(x => x.ConversationId == conversationId)
            .ToListAsync(cancellationToken);
        var messages = await db.ConversationMessages
            .Where(x => x.ConversationId == conversationId)
            .ToListAsync(cancellationToken);
        db.GenerationJobs.RemoveRange(jobs);
        db.ConversationMessages.RemoveRange(messages);
        db.Conversations.Remove(conversation);
        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(userId, "delete_conversation", "conversation", conversationId.ToString(), $"删除会话 {conversation.Title}", cancellationToken);
        return NoContent();
    }

    [HttpPost("jobs")]
    public async Task<ActionResult<GenerationJobDto>> CreateJob(SendMessageRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { message = "消息不能为空。" });
        }

        var job = await generationJobRunner.CreateAsync(User.GetUserId(), request, cancellationToken);
        await generationJobQueue.EnqueueAsync(job.Id, cancellationToken);
        return Ok(job);
    }

    [HttpGet("jobs/running")]
    public async Task<ActionResult<IReadOnlyList<GenerationJobDto>>> RunningJobs([FromQuery] long? conversationId, CancellationToken cancellationToken)
    {
        return Ok(await generationJobRunner.ListRunningAsync(User.GetUserId(), conversationId, cancellationToken));
    }

    [HttpGet("jobs")]
    public async Task<ActionResult<IReadOnlyList<GenerationJobSummaryDto>>> Jobs(
        [FromQuery] long? conversationId,
        [FromQuery] string? status,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        return Ok(await generationJobRunner.ListAsync(User.GetUserId(), conversationId, status, limit <= 0 ? 50 : limit, cancellationToken));
    }

    [HttpGet("jobs/{jobId:long}")]
    public async Task<ActionResult<GenerationJobDto>> Job(long jobId, CancellationToken cancellationToken)
    {
        var job = await generationJobRunner.GetAsync(User.GetUserId(), jobId, cancellationToken);
        return job is null ? NotFound(new { message = "任务不存在或无权访问。" }) : Ok(job);
    }

    [HttpGet("jobs/{jobId:long}/stream")]
    public async Task StreamJob(long jobId, [FromQuery] int sinceVersion, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        PrepareSseResponse();
        var lastVersion = sinceVersion;

        while (!cancellationToken.IsCancellationRequested)
        {
            var job = await generationJobRunner.GetAsync(userId, jobId, cancellationToken);
            if (job is null)
            {
                await WriteEventAsync(SseEventNames.Done, new { missing = true }, cancellationToken);
                return;
            }

            if (job.Version > lastVersion)
            {
                await WriteEventAsync(SseEventNames.Job, job, cancellationToken);
                lastVersion = job.Version;
            }

            if (GenerationJobStatuses.IsTerminal(job.Status))
            {
                await WriteEventAsync(SseEventNames.Done, job, cancellationToken);
                return;
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    [HttpPost("jobs/{jobId:long}/cancel")]
    public async Task<ActionResult<GenerationJobDto>> CancelJob(long jobId, CancellationToken cancellationToken)
    {
        var job = await generationJobRunner.CancelAsync(User.GetUserId(), jobId, cancellationToken);
        return job is null ? NotFound(new { message = "任务不存在或无权取消。" }) : Ok(job);
    }

    [HttpPost("jobs/{jobId:long}/retry")]
    public async Task<ActionResult<GenerationJobDto>> RetryJob(long jobId, CancellationToken cancellationToken)
    {
        try
        {
            var job = await generationJobRunner.RetryAsync(User.GetUserId(), jobId, cancellationToken);
            if (job is null)
            {
                return NotFound(new { message = "任务不存在或无权重试。" });
            }

            await generationJobQueue.EnqueueAsync(job.Id, cancellationToken);
            return Ok(job);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("retry-image")]
    public async Task<ActionResult<RetryImageResponse>> RetryImage(RetryImageRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var provider = await providerResolver.GetActiveProviderAsync(userId, cancellationToken);
        var imageModel = providerResolver.GetEnabledModel(provider, AiModelTypes.Image);
        var image = await imageGenerationService.GenerateSingleArticleImageAsync(
            provider,
            imageModel,
            request.Title,
            request.ArticleMarkdown,
            null,
            cancellationToken);
        return Ok(new RetryImageResponse(image.Title, image.Url, image.Error));
    }

    [HttpPost("retry-image/stream")]
    public async Task RetryImageStream(RetryImageRequest request, CancellationToken cancellationToken)
    {
        PrepareSseResponse();

        var userId = User.GetUserId();
        var provider = await providerResolver.GetActiveProviderAsync(userId, cancellationToken);
        var imageModel = providerResolver.GetEnabledModel(provider, AiModelTypes.Image);
        await WriteEventAsync(SseEventNames.Status, new { text = imageModel is null ? "未配置可用的生图模型。" : "正在准备生成图片。" }, cancellationToken);
        await foreach (var image in imageGenerationService.GenerateSingleArticleImageStreamAsync(
            provider,
            imageModel,
            request.Title,
            request.ArticleMarkdown,
            null,
            cancellationToken))
        {
            await WriteEventAsync(SseEventNames.Image, new GenerateImageResponse(image.Title, image.Prompt, image.Url, image.Error, image.IsPartial), cancellationToken);
        }

        await WriteEventAsync(SseEventNames.Done, new { }, cancellationToken);
    }

    [HttpPost("generate-image")]
    public async Task<ActionResult<GenerateImageResponse>> GenerateImage(GenerateImageRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var provider = await providerResolver.GetActiveProviderAsync(userId, cancellationToken);
        var imageModel = providerResolver.GetEnabledModel(provider, AiModelTypes.Image);
        var image = await imageGenerationService.GenerateFromPromptAsync(
            provider,
            imageModel,
            string.IsNullOrWhiteSpace(request.Title) ? "插入图片" : request.Title,
            request.Prompt,
            null,
            cancellationToken);
        return Ok(new GenerateImageResponse(image.Title, image.Prompt, image.Url, image.Error, image.IsPartial));
    }

    [HttpPost("generate-image/stream")]
    public async Task GenerateImageStream(GenerateImageRequest request, CancellationToken cancellationToken)
    {
        PrepareSseResponse();

        var userId = User.GetUserId();
        var provider = await providerResolver.GetActiveProviderAsync(userId, cancellationToken);
        var imageModel = providerResolver.GetEnabledModel(provider, AiModelTypes.Image);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "生成图片" : request.Title;

        await WriteEventAsync(SseEventNames.Status, new { text = imageModel is null ? "未配置可用的生图模型。" : "正在准备生成图片。" }, cancellationToken);
        await foreach (var image in imageGenerationService.GenerateFromPromptStreamAsync(provider, imageModel, title, request.Prompt, null, cancellationToken))
        {
            await WriteEventAsync(SseEventNames.Image, new GenerateImageResponse(image.Title, image.Prompt, image.Url, image.Error, image.IsPartial), cancellationToken);
        }

        await WriteEventAsync(SseEventNames.Done, new { }, cancellationToken);
    }

    [HttpPost("edit-document")]
    public async Task<ActionResult<EditDocumentResponse>> EditDocument(EditDocumentRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var provider = await providerResolver.GetActiveProviderAsync(userId, cancellationToken);
        var chatModel = providerResolver.GetEnabledModel(provider, AiModelTypes.Chat);

        var target = string.IsNullOrWhiteSpace(request.Selection) ? "全文" : "选中文本";
        var userPrompt = $"""
            请对下面这篇中文 Markdown 图文文章执行编辑任务。
            编辑对象：{target}
            编辑指令：{request.Instruction}

            要求：
            - 只输出编辑后的 Markdown 正文，不要解释过程。
            - 保留已有 Markdown 图片、图片链接和 <!-- image-prompt:... --> 注释。
            - 如果只编辑选中文本，只输出编辑后的选中文本，不要输出全文。
            - 不要编造不存在的事实。

            选中文本：
            {request.Selection}

            当前全文：
            {request.Content}
            """;

        var turns = new[]
        {
            new ChatTurn("system", "你是专业中文内容编辑，擅长公众号文章排版、标题优化、结构梳理和表达润色。"),
            new ChatTurn("user", userPrompt)
        };

        var builder = new StringBuilder();
        await foreach (var chunk in chatClient.StreamReplyAsync(provider, chatModel, turns, new AgentOptionsDto("quick", "wechat", "article", 0.4m, 0, false, false), cancellationToken))
        {
            builder.Append(chunk);
        }

        var content = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            return BadRequest(new { message = "AI 没有返回有效编辑结果。" });
        }

        return Ok(new EditDocumentResponse(content));
    }

    [HttpPut("{conversationId:long}/messages/{messageId:long}")]
    public async Task<IActionResult> UpdateMessage(long conversationId, long messageId, UpdateMessageRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var message = await db.ConversationMessages
            .Include(x => x.Conversation)
            .FirstOrDefaultAsync(x => x.Id == messageId && x.ConversationId == conversationId && x.Conversation!.UserId == userId, cancellationToken);
        if (message is null)
        {
            return NotFound(new { message = "消息不存在。" });
        }

        if (message.Role != "assistant")
        {
            return BadRequest(new { message = "只能编辑助手生成的文档。" });
        }

        message.Content = request.Content;
        message.Conversation!.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "文档已保存。" });
    }

    [HttpPost("{conversationId:long}/messages/{messageId:long}/feedback")]
    public async Task<IActionResult> FeedbackMessage(long conversationId, long messageId, MessageFeedbackRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var exists = await db.ConversationMessages
            .AsNoTracking()
            .AnyAsync(x => x.Id == messageId && x.ConversationId == conversationId && x.Conversation!.UserId == userId, cancellationToken);
        if (!exists)
        {
            return NotFound(new { message = "消息不存在。" });
        }

        var type = string.IsNullOrWhiteSpace(request.Type) ? "report" : request.Type.Trim();
        var detail = string.IsNullOrWhiteSpace(request.Detail) ? "用户在消息菜单提交反馈。" : request.Detail.Trim();
        await auditLogger.LogAsync(userId, $"message_feedback_{type}", "conversation_message", messageId.ToString(), detail, cancellationToken);
        return Ok(new { message = "反馈已记录。" });
    }

    [HttpPost("stream")]
    public async Task Stream(SendMessageRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        PrepareSseResponse();

        var conversationId = await conversationService.EnsureConversationAsync(userId, request.ConversationId, request.Content, cancellationToken);
        await WriteEventAsync(SseEventNames.Conversation, new { conversationId }, cancellationToken);
        await WriteThinkingAsync(request, "已创建或载入会话，开始整理本次任务。", cancellationToken);

        var attachmentEnrichedRequest = await TryEnrichRequestWithAttachmentsAsync(request, cancellationToken);
        var enrichedRequest = await TryEnrichRequestWithWebPagesAsync(attachmentEnrichedRequest, conversationId, userId, cancellationToken);
        if (enrichedRequest is null)
        {
            return;
        }

        var provider = await providerResolver.GetActiveProviderAsync(userId, cancellationToken);
        var chatModel = providerResolver.GetEnabledModel(provider, AiModelTypes.Chat);
        var imageModel = providerResolver.GetEnabledModel(provider, AiModelTypes.Image);

        var intent = await intentRouter.RouteAsync(enrichedRequest, provider, chatModel, cancellationToken);
        await WriteEventAsync(SseEventNames.Mode, new { type = intent.Type, mode = intent.Mode, title = intent.Title, confidence = intent.Confidence }, cancellationToken);
        await WriteThinkingAsync(request, $"意图识别：{intent.Type} / {intent.Confidence:0.00} / {intent.Title}", cancellationToken);
        if (intent.Confidence < 0.65)
        {
            var confirmText = BuildIntentConfirmationText(intent);
            await WriteEventAsync(SseEventNames.Delta, new { text = confirmText }, cancellationToken);
            await conversationService.AddAssistantMessageAsync(userId, conversationId, confirmText, cancellationToken, ConversationIntentCatalog.Get(AiModelTypes.Chat).Type);
            await WriteEventAsync(SseEventNames.Done, new { conversationId }, cancellationToken);
            return;
        }

        await WriteThinkingAsync(request, $"任务类型：{BuildIntentLabel(intent)}。", cancellationToken);
        await WriteThinkingAsync(request, BuildThinkingSummary(request), cancellationToken);

        if (intent.Type == AiModelTypes.Image)
        {
            await WriteThinkingAsync(request, imageModel is null
                ? "还没有配置可用的图片生成能力。"
                : "已准备图片生成能力。", cancellationToken);
        }
        else
        {
            await WriteThinkingAsync(request, chatModel is null
                ? "还没有配置可用的对话生成能力，将使用演示回复。"
                : "已准备内容生成能力。", cancellationToken);
        }

        if (request.Options?.EnableWebSearch == true)
        {
            await WriteThinkingAsync(request, "智能搜索已开启。", cancellationToken);
        }

        if (intent.Type == AiModelTypes.Image)
        {
            await HandleDirectImageGenerationAsync(
                request,
                intent,
                provider,
                imageModel,
                conversationId,
                userId,
                cancellationToken);
            return;
        }

        var turns = await BuildTurnsForIntentAsync(userId, conversationId, enrichedRequest, intent, cancellationToken);
        var full = new StringBuilder();
        await WriteThinkingAsync(request, "开始生成回复。", cancellationToken);
        try
        {
            await foreach (var chunk in chatClient.StreamReplyAsync(provider, chatModel, turns, request.Options, cancellationToken))
            {
                full.Append(chunk);
                await WriteEventAsync(SseEventNames.Delta, new { text = chunk }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            var errorText = $"生成失败：{ex.Message}";
            full.Append(errorText);
            await WriteEventAsync(SseEventNames.Delta, new { text = errorText }, cancellationToken);
            await conversationService.AddAssistantMessageAsync(userId, conversationId, full.ToString(), cancellationToken, intent.Type);
            await WriteEventAsync(SseEventNames.Done, new { conversationId }, cancellationToken);
            return;
        }

        if (ShouldGenerateImages(intent, enrichedRequest, full.ToString()))
        {
            if (imageModel is null)
            {
                await WriteThinkingAsync(request, "需要配图，但还没有配置可用的生图模型。", cancellationToken);
                var missingImageNote = "\n\n> 当前尚未配置生图模型，图片位置已保留。";
                full.Append(missingImageNote);
                await WriteEventAsync(SseEventNames.Delta, new { text = missingImageNote }, cancellationToken);
            }
            else
            {
                await WriteThinkingAsync(request, "正在生成配图。", cancellationToken);
                var articleWithoutImages = full.ToString();
                var images = new List<GeneratedArticleImage>();
                await foreach (var image in imageGenerationService.GenerateArticleImagesStreamAsync(
                    provider,
                    imageModel,
                    enrichedRequest.Content,
                    articleWithoutImages,
                    request.Options,
                    cancellationToken))
                {
                    var existingIndex = images.FindIndex(x => string.Equals(x.Title, image.Title, StringComparison.OrdinalIgnoreCase));
                    if (existingIndex >= 0)
                    {
                        images[existingIndex] = image;
                    }
                    else
                    {
                        images.Add(image);
                    }

                    var partialArticle = ArticleMarkdownImageComposer.TrimMarkdownTitle(
                        ArticleMarkdownImageComposer.PlaceImagesInArticlePreview(articleWithoutImages, images),
                        30);
                    await WriteEventAsync(SseEventNames.Replace, new { text = partialArticle }, cancellationToken);
                    await WriteThinkingAsync(request, string.IsNullOrWhiteSpace(image.Url)
                        ? $"{image.Title} 生成失败：{image.Error}"
                        : image.IsPartial
                            ? $"{image.Title} 正在生成，已收到预览图。"
                            : $"{image.Title} 已生成并放入文章。", cancellationToken);
                }

                var articleWithImages = ArticleMarkdownImageComposer.TrimMarkdownTitle(
                    ArticleMarkdownImageComposer.PlaceImagesInArticle(articleWithoutImages, images),
                    30);
                full.Clear().Append(articleWithImages);
                await WriteEventAsync(SseEventNames.Replace, new { text = articleWithImages }, cancellationToken);
                await WriteThinkingAsync(request, BuildImageThinking(images), cancellationToken);
            }
        }

        var finalContent = ArticleMarkdownImageComposer.TrimMarkdownTitle(full.ToString(), 30);
        full.Clear().Append(finalContent);
        await conversationService.AddAssistantMessageAsync(userId, conversationId, finalContent, cancellationToken, intent.Type);
        await WriteThinkingAsync(request, "内容生成完成，已保存到会话历史。", cancellationToken);
        await WriteEventAsync(SseEventNames.Done, new { conversationId }, cancellationToken);
    }

    private async Task<IReadOnlyList<ChatTurn>> BuildTurnsForIntentAsync(
        long userId,
        long conversationId,
        SendMessageRequest request,
        ConversationIntent intent,
        CancellationToken cancellationToken)
    {
        var turns = await conversationService.BuildTurnsAsync(userId, conversationId, request, cancellationToken);
        var instruction = ConversationIntentCatalog.Get(intent.Type).ExecutionInstruction;
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return turns;
        }

        return turns.Select((turn, index) => index == 0 && turn.Role == "system"
            ? new ChatTurn("system", turn.Content + Environment.NewLine + Environment.NewLine + instruction)
            : turn).ToList();
    }

    private static bool ShouldGenerateImages(ConversationIntent intent, SendMessageRequest request, string generatedContent)
    {
        if (intent.Type is not ("article" or "article_image"))
        {
            return false;
        }

        var text = request.Content;
        if (ArticleMarkdownImageComposer.ContainsImagePlaceholders(generatedContent) ||
            ArticleMarkdownImageComposer.ContainsImagePlaceholders(text))
        {
            return true;
        }

        return intent.Type == "article_image";
    }

    private async Task HandleDirectImageGenerationAsync(
        SendMessageRequest originalRequest,
        ConversationIntent intent,
        AiProvider? provider,
        AiModel? imageModel,
        long conversationId,
        long userId,
        CancellationToken cancellationToken)
    {
        await WriteThinkingAsync(originalRequest, imageModel is null
            ? "还没有配置可用的图片生成能力。"
            : "识别为直接生图请求，开始准备图片生成。", cancellationToken);

        if (imageModel is null)
        {
            var missingImageNote = "> 当前尚未配置生图模型，请先在设置里配置可用的图片模型。";
            await WriteEventAsync(SseEventNames.Delta, new { text = missingImageNote }, cancellationToken);
            await conversationService.AddAssistantMessageAsync(userId, conversationId, missingImageNote, cancellationToken, intent.Type);
            await WriteEventAsync(SseEventNames.Done, new { conversationId }, cancellationToken);
            return;
        }

        var title = intent.Title;
        var prompt = intent.Prompt;
        var statusText = $"正在生成图片：{title}\n\n";
        await WriteEventAsync(SseEventNames.Delta, new { text = statusText }, cancellationToken);

        GeneratedArticleImage? generated = null;
        await foreach (var image in imageGenerationService.GenerateFromPromptStreamAsync(provider, imageModel, title, prompt, originalRequest.Options, cancellationToken))
        {
            generated = image;
            var content = BuildDirectImageMessage(image);
            await WriteEventAsync(SseEventNames.Replace, new { text = content }, cancellationToken);
            await WriteThinkingAsync(originalRequest, string.IsNullOrWhiteSpace(image.Url)
                ? $"图片生成失败：{image.Error}"
                : "图片已生成。", cancellationToken);
        }

        var finalContent = generated is null
            ? "> 图片生成失败：生图接口没有返回结果。"
            : BuildDirectImageMessage(generated);
        await conversationService.AddAssistantMessageAsync(userId, conversationId, finalContent, cancellationToken, intent.Type);
        await WriteThinkingAsync(originalRequest, "图片生成完成，已保存到会话历史。", cancellationToken);
        await WriteEventAsync(SseEventNames.Done, new { conversationId }, cancellationToken);
    }

    private static string BuildDirectImageMessage(GeneratedArticleImage image)
    {
        if (!string.IsNullOrWhiteSpace(image.Url))
        {
            return ArticleMarkdownImageComposer.BuildInlineImageMarkdown(image);
        }

        return $"> 图片生成失败：{image.Error}";
    }

    private static string BuildIntentLabel(ConversationIntent intent) =>
        ConversationIntentCatalog.Get(intent.Type).Label;

    private static string BuildIntentConfirmationText(ConversationIntent intent)
    {
        return $"""
            我还不太确定你这次想让我怎么处理。

            我当前判断是：{BuildIntentLabel(intent)}（置信度 {intent.Confidence:0.00}）。

            你可以直接补一句：
            - “按问答回答”
            - “写成文章”
            - “写成文档”
            - “直接生成图片”
            - “只给这篇文章配图，不要改正文”
            """;
    }

    private async Task<SendMessageRequest?> TryEnrichRequestWithWebPagesAsync(
        SendMessageRequest request,
        long conversationId,
        long userId,
        CancellationToken cancellationToken)
    {
        var urls = webPageContentService.ExtractUrls(request.Content);
        if (urls.Count == 0)
        {
            return request;
        }

        await WriteThinkingAsync(request, $"检测到 {urls.Count} 个链接，正在读取网页正文。", cancellationToken);
        var results = await webPageContentService.FetchAsync(urls, cancellationToken);
        var pages = results.Where(x => x.Page is not null).Select(x => x.Page!).ToList();
        var failures = results.Where(x => x.Page is null).ToList();

        foreach (var page in pages)
        {
            await WriteThinkingAsync(request, $"已读取链接：{page.Title ?? page.Url}。", cancellationToken);
        }

        if (pages.Count == 0)
        {
            var errorText = BuildWebPageFailureMessage(failures);
            await WriteEventAsync(SseEventNames.Delta, new { text = errorText }, cancellationToken);
            await conversationService.AddAssistantMessageAsync(userId, conversationId, errorText, cancellationToken);
            await WriteEventAsync(SseEventNames.Done, new { conversationId }, cancellationToken);
            return null;
        }

        var builder = new StringBuilder(request.Content);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("以下是系统已读取到的网页正文，请严格基于这些内容生成，不要假装读取未成功的链接：");
        foreach (var page in pages)
        {
            builder.AppendLine();
            builder.AppendLine($"## 来源：{page.Title ?? page.Url}");
            builder.AppendLine($"URL：{page.Url}");
            builder.AppendLine(page.Content);
        }

        if (failures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("以下链接读取失败，不要基于失败链接编造内容：");
            foreach (var failure in failures)
            {
                builder.AppendLine($"- {failure.Url}：{failure.Error}");
            }
        }

        return request with { Content = builder.ToString() };
    }

    private async Task<SendMessageRequest> TryEnrichRequestWithAttachmentsAsync(
        SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Attachments is not { Count: > 0 })
        {
            return request;
        }

        await WriteThinkingAsync(request, $"检测到 {request.Attachments.Count} 个附件，正在提取可读内容。", cancellationToken);
        var results = await attachmentContentService.ExtractAsync(request.Attachments, cancellationToken);
        var readable = results.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();
        var failed = results.Where(x => string.IsNullOrWhiteSpace(x.Text) && !string.IsNullOrWhiteSpace(x.Error)).ToList();

        foreach (var item in readable)
        {
            await WriteThinkingAsync(request, $"已读取附件：{item.FileName}。", cancellationToken);
        }

        foreach (var item in failed)
        {
            await WriteThinkingAsync(request, $"{item.FileName} 未能展开：{item.Error}", cancellationToken);
        }

        if (readable.Count == 0)
        {
            return request;
        }

        var builder = new StringBuilder(request.Content);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("以下是系统已从上传附件中提取到的可读内容，请严格结合这些内容处理用户需求；不要声称读取了未成功解析的文件：");
        foreach (var item in readable)
        {
            builder.AppendLine();
            builder.AppendLine($"## 附件：{item.FileName}");
            builder.AppendLine($"类型：{item.ContentType}，大小：{item.Size} bytes，URL：{item.Url}");
            builder.AppendLine(item.Text);
        }

        if (failed.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("以下附件未能提取正文，只能作为文件名和类型参考：");
            foreach (var item in failed)
            {
                builder.AppendLine($"- {item.FileName}：{item.Error}");
            }
        }

        return request with { Content = builder.ToString() };
    }

    private static string BuildWebPageFailureMessage(IReadOnlyList<WebPageFetchResult> failures)
    {
        var builder = new StringBuilder();
        builder.AppendLine("我没有成功读取到链接正文，所以不能基于它生成文章，避免编造内容。");
        builder.AppendLine();
        builder.AppendLine("读取结果：");
        foreach (var failure in failures)
        {
            builder.AppendLine($"- {failure.Url}：{failure.Error}");
        }
        builder.AppendLine();
        builder.AppendLine("你可以稍后重试，或者把网页正文复制进来，我就能继续生成图文文章。");
        return builder.ToString();
    }

    private static string BuildImageThinking(IReadOnlyList<GeneratedArticleImage> images)
    {
        var successCount = images.Count(x => !string.IsNullOrWhiteSpace(x.Url));
        var failedCount = images.Count - successCount;
        return failedCount == 0
            ? $"已生成 {successCount} 张图片，并放入文章对应位置。"
            : $"已生成 {successCount} 张图片，{failedCount} 张失败；失败位置已在文章中标出。";
    }

    private async Task WriteThinkingAsync(SendMessageRequest request, string text, CancellationToken cancellationToken)
    {
        if (request.Options?.ShowThinking != true)
        {
            return;
        }

        await WriteEventAsync(SseEventNames.Thinking, new { text }, cancellationToken);
    }

    private static string BuildThinkingSummary(SendMessageRequest request)
    {
        var attachmentCount = request.Attachments?.Count ?? 0;
        var options = request.Options;
        var thinking = options?.ThinkingMode?.Trim().ToLowerInvariant() switch
        {
            "expert" => "专家思考已开启",
            "think" or "deep" => "思考模式已开启",
            _ => "快速模式"
        };
        var search = options?.EnableWebSearch == true ? "智能搜索已开启" : "未开启智能搜索";
        return $"任务状态：{thinking}，{search}，附件 {attachmentCount} 个。";
    }

    private void PrepareSseResponse()
    {
        SseResponseWriter.Prepare(Response);
    }

    private async Task WriteEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        await SseResponseWriter.WriteAsync(Response, eventName, payload, cancellationToken);
    }
}

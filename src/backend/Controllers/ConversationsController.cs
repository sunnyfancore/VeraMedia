using System.Text;
using System.Diagnostics;
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
[Route("api/conversations")]
public sealed class ConversationsController(
    AppDbContext db,
    IConversationService conversationService,
    IConversationIntentRouter intentRouter,
    IAiProviderResolver providerResolver,
    IAiChatClient chatClient,
    IImageGenerationService imageGenerationService,
    IWebPageContentService webPageContentService,
    IGenerationJobRunner generationJobRunner,
    IGenerationJobQueue generationJobQueue,
    IOfficeExportService officeExportService,
    IAuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConversationSummary>>> List(CancellationToken cancellationToken)
    {
        return Ok(await conversationService.ListAsync(User.GetUserId(), cancellationToken));
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
            var video = await CreatePptVideoAsync(title, message.Content, cancellationToken);
            return File(video.Content, video.ContentType, video.FileName);
        }

        var file = string.Equals(format, "pptx", StringComparison.OrdinalIgnoreCase)
            ? officeExportService.CreatePptx(title, message.Content)
            : officeExportService.CreateDocx(title, message.Content);
        return File(file.Content, file.ContentType, file.FileName);
    }

    private async Task<OfficeExportFile> CreatePptVideoAsync(string title, string markdown, CancellationToken cancellationToken)
    {
        var converter = ResolvePptVideoConverter();
        if (converter is null)
        {
            throw new InvalidOperationException("当前环境未安装 PPT 转视频工具。请确认容器内存在 export-ppt-video。");
        }

        var pptx = officeExportService.CreatePptx(title, markdown);
        var workDir = Path.Combine(Path.GetTempPath(), "veramedia-ppt-video", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var pptxPath = Path.Combine(workDir, pptx.FileName);
            await System.IO.File.WriteAllBytesAsync(pptxPath, pptx.Content, cancellationToken);

            var outputDir = Path.Combine(workDir, "video");
            var startInfo = new ProcessStartInfo
            {
                FileName = converter,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(pptxPath);
            startInfo.ArgumentList.Add(outputDir);
            startInfo.ArgumentList.Add("5");
            startInfo.ArgumentList.Add("zh");

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 PPT 转视频工具。");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "PPT 转视频失败。" : stderr);
            }

            var videoPath = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(videoPath) || !System.IO.File.Exists(videoPath))
            {
                videoPath = Directory.GetFiles(outputDir, "*.mp4", SearchOption.AllDirectories).FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(videoPath) || !System.IO.File.Exists(videoPath))
            {
                throw new InvalidOperationException("PPT 转视频完成，但没有找到生成的视频文件。");
            }

            var bytes = await System.IO.File.ReadAllBytesAsync(videoPath, cancellationToken);
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

    private static string? ResolvePptVideoConverter()
    {
        if (System.IO.File.Exists("/usr/local/bin/export-ppt-video")) return "/usr/local/bin/export-ppt-video";
        var local = Path.Combine(AppContext.BaseDirectory, "scripts", "export-ppt-video.sh");
        return System.IO.File.Exists(local) ? local : null;
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
        await foreach (var image in imageGenerationService.GenerateFromPromptStreamAsync(provider, imageModel, title, request.Prompt, cancellationToken))
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

    [HttpPost("stream")]
    public async Task Stream(SendMessageRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        PrepareSseResponse();

        var conversationId = await conversationService.EnsureConversationAsync(userId, request.ConversationId, request.Content, cancellationToken);
        await WriteEventAsync(SseEventNames.Conversation, new { conversationId }, cancellationToken);
        await WriteThinkingAsync(request, "已创建或载入会话，开始整理本次任务。", cancellationToken);

        var enrichedRequest = await TryEnrichRequestWithWebPagesAsync(request, conversationId, userId, cancellationToken);
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
        await foreach (var image in imageGenerationService.GenerateFromPromptStreamAsync(provider, imageModel, title, prompt, cancellationToken))
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


using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public sealed class GenerationJobRunner(
    AppDbContext db,
    IConversationService conversationService,
    IConversationIntentRouter intentRouter,
    IAiProviderResolver providerResolver,
    IAiChatClient chatClient,
    IImageGenerationService imageGenerationService,
    IWebPageContentService webPageContentService,
    IAppSettingsService appSettingsService) : IGenerationJobRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MinStreamPersistInterval = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan MinCancelCheckInterval = TimeSpan.FromMilliseconds(1500);
    private DateTime lastPersistAt = DateTime.MinValue;
    private DateTime lastCancelCheckAt = DateTime.MinValue;

    public async Task<GenerationJobDto> CreateAsync(long userId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        Conversation conversation;
        if (request.ConversationId is > 0)
        {
            conversation = await db.Conversations.FirstOrDefaultAsync(x => x.Id == request.ConversationId && x.UserId == userId, cancellationToken)
                ?? throw new InvalidOperationException("会话不存在。");
        }
        else
        {
            conversation = new Conversation
            {
                UserId = userId,
                Title = BuildTitle(request.Content),
                CreatedAt = now
            };
            db.Conversations.Add(conversation);
        }

        conversation.UpdatedAt = now;
        var userMessage = new ConversationMessage
        {
            Conversation = conversation,
            Role = "user",
            Content = request.Content,
            CreatedAt = now
        };
        var assistantMessage = new ConversationMessage
        {
            Conversation = conversation,
            Role = "assistant",
            Content = "",
            MetadataJson = "{}",
            CreatedAt = now.AddTicks(1)
        };
        db.ConversationMessages.AddRange(userMessage, assistantMessage);
        await db.SaveChangesAsync(cancellationToken);

        var job = new GenerationJob
        {
            UserId = userId,
            ConversationId = conversation.Id,
            UserMessageId = userMessage.Id,
            AssistantMessageId = assistantMessage.Id,
            RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            Status = GenerationJobStatuses.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(job);
    }

    public async Task<IReadOnlyList<GenerationJobDto>> ListRunningAsync(long userId, long? conversationId, CancellationToken cancellationToken)
    {
        var terminal = new[] { GenerationJobStatuses.Completed, GenerationJobStatuses.Failed, GenerationJobStatuses.Canceled };
        var query = db.GenerationJobs.AsNoTracking()
            .Where(x => x.UserId == userId && !terminal.Contains(x.Status));
        if (conversationId is > 0)
        {
            query = query.Where(x => x.ConversationId == conversationId);
        }

        var jobs = await query.OrderBy(x => x.Id).ToListAsync(cancellationToken);
        return jobs.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<GenerationJobSummaryDto>> ListAsync(long userId, long? conversationId, string? status, int limit, CancellationToken cancellationToken)
    {
        var query = db.GenerationJobs.AsNoTracking()
            .Include(x => x.Conversation)
            .Where(x => x.UserId == userId);
        if (conversationId is > 0)
        {
            query = query.Where(x => x.ConversationId == conversationId);
        }

        if (!string.IsNullOrWhiteSpace(status) && status != "all")
        {
            query = query.Where(x => x.Status == status);
        }

        var take = Math.Clamp(limit, 1, 100);
        var jobs = await query
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        return jobs.Select(ToSummaryDto).ToList();
    }

    public async Task<GenerationJobDto?> GetAsync(long userId, long jobId, CancellationToken cancellationToken)
    {
        var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == jobId && x.UserId == userId, cancellationToken);
        return job is null ? null : ToDto(job);
    }

    public async Task<GenerationJobDto?> CancelAsync(long userId, long jobId, CancellationToken cancellationToken)
    {
        var job = await db.GenerationJobs
            .Include(x => x.AssistantMessage)
            .Include(x => x.Conversation)
            .FirstOrDefaultAsync(x => x.Id == jobId && x.UserId == userId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        if (!GenerationJobStatuses.IsTerminal(job.Status))
        {
            job.Status = GenerationJobStatuses.Canceled;
            job.Error = "任务已取消。";
            job.CompletedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            job.Version++;
            if (string.IsNullOrWhiteSpace(job.Content))
            {
                job.Content = "任务已取消。";
            }

            if (job.AssistantMessage is not null)
            {
                job.AssistantMessage.Content = job.Content;
            }

            if (job.Conversation is not null)
            {
                job.Conversation.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return ToDto(job);
    }

    public async Task<GenerationJobDto?> RetryAsync(long userId, long jobId, CancellationToken cancellationToken)
    {
        var source = await db.GenerationJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == jobId && x.UserId == userId, cancellationToken);
        if (source is null)
        {
            return null;
        }

        if (!GenerationJobStatuses.IsTerminal(source.Status))
        {
            throw new InvalidOperationException("任务还在进行中，不能重复重试。");
        }

        var request = JsonSerializer.Deserialize<SendMessageRequest>(source.RequestJson, JsonOptions)
            ?? new SendMessageRequest("", source.ConversationId, null, null);
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new InvalidOperationException("原任务请求内容为空，无法重试。");
        }

        var retryRequest = request with { ConversationId = source.ConversationId };
        return await CreateAsync(userId, retryRequest, cancellationToken);
    }

    public async Task RunAsync(long jobId, CancellationToken cancellationToken)
    {
        var job = await db.GenerationJobs
            .Include(x => x.AssistantMessage)
            .Include(x => x.Conversation)
            .FirstOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || GenerationJobStatuses.IsTerminal(job.Status))
        {
            return;
        }

        var request = JsonSerializer.Deserialize<SendMessageRequest>(job.RequestJson, JsonOptions)
            ?? new SendMessageRequest("", job.ConversationId, null, null);

        try
        {
            job.Status = GenerationJobStatuses.Running;
            job.StartedAt ??= DateTime.UtcNow;
            await PersistAsync(job, force: true, cancellationToken);

            await AddThinkingAsync(job, request, "已创建或载入会话，开始整理本次任务。", cancellationToken);
            var enrichedRequest = await TryEnrichRequestWithWebPagesAsync(job, request, cancellationToken);
            if (enrichedRequest is null)
            {
                await CompleteAsync(job, cancellationToken);
                return;
            }

            var provider = await providerResolver.GetActiveProviderAsync(job.UserId, cancellationToken);
            var chatModel = providerResolver.GetEnabledModel(provider, AiModelTypes.Chat);
            var imageModel = providerResolver.GetEnabledModel(provider, AiModelTypes.Image);

            var intent = await intentRouter.RouteAsync(enrichedRequest, provider, chatModel, cancellationToken);
            await SetModeAsync(job, intent.Type, cancellationToken);
            await AddThinkingAsync(job, request, $"意图识别：{intent.Type} / {intent.Confidence:0.00} / {intent.Title}", cancellationToken);
            if (intent.Confidence < 0.65)
            {
                await ReplaceContentAsync(job, BuildIntentConfirmationText(intent), force: true, cancellationToken);
                await SetModeAsync(job, ConversationIntentCatalog.Get(AiModelTypes.Chat).Type, cancellationToken);
                await CompleteAsync(job, cancellationToken);
                return;
            }

            await AddThinkingAsync(job, request, $"任务类型：{BuildIntentLabel(intent)}。", cancellationToken);
            await AddThinkingAsync(job, request, BuildThinkingSummary(request), cancellationToken);
            await AddThinkingAsync(job, request, intent.Type == AiModelTypes.Image
                ? imageModel is null ? "还没有配置可用的图片生成能力。" : "已准备图片生成能力。"
                : chatModel is null ? "还没有配置可用的对话生成能力，将使用演示回复。" : "已准备内容生成能力。", cancellationToken);

            if (request.Options?.EnableWebSearch == true)
            {
                await AddThinkingAsync(job, request, "智能搜索已开启。", cancellationToken);
            }

            if (intent.Type == AiModelTypes.Image)
            {
                await HandleDirectImageGenerationAsync(job, request, intent, provider, imageModel, cancellationToken);
                return;
            }

            await GenerateTextAsync(job, request, enrichedRequest, intent, provider, chatModel, imageModel, cancellationToken);
        }
        catch (JobCanceledException)
        {
            // The cancel endpoint has already persisted the terminal state.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.Status = GenerationJobStatuses.Canceled;
            job.Error = "服务正在停止，任务已取消。";
            await PersistAsync(job, force: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            job.Status = GenerationJobStatuses.Failed;
            job.Error = ex.Message;
            var text = string.IsNullOrWhiteSpace(job.Content) ? $"生成失败：{ex.Message}" : $"{job.Content}\n\n> 生成失败：{ex.Message}";
            await ReplaceContentAsync(job, text, force: true, CancellationToken.None);
            await PersistAsync(job, force: true, CancellationToken.None);
        }
    }

    private async Task GenerateTextAsync(
        GenerationJob job,
        SendMessageRequest originalRequest,
        SendMessageRequest enrichedRequest,
        ConversationIntent intent,
        AiProvider? provider,
        AiModel? chatModel,
        AiModel? imageModel,
        CancellationToken cancellationToken)
    {
        var turns = await BuildTurnsForIntentAsync(job.UserId, job.ConversationId, enrichedRequest, intent, cancellationToken);
        var full = new StringBuilder();
        await AddThinkingAsync(job, originalRequest, "开始生成回复。", cancellationToken);
        try
        {
            await foreach (var chunk in chatClient.StreamReplyAsync(provider, chatModel, turns, originalRequest.Options, cancellationToken))
            {
                full.Append(chunk);
                await ReplaceContentAsync(job, full.ToString(), force: false, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            job.Status = GenerationJobStatuses.Failed;
            job.Error = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            full.Append($"生成失败：{ex.Message}");
            await ReplaceContentAsync(job, full.ToString(), force: true, cancellationToken);
            await PersistAsync(job, force: true, cancellationToken);
            return;
        }

        if (ShouldGenerateImages(intent, enrichedRequest, full.ToString()))
        {
            if (imageModel is null)
            {
                await AddThinkingAsync(job, originalRequest, "需要配图，但还没有配置可用的生图模型。", cancellationToken);
                full.Append("\n\n> 当前尚未配置生图模型，图片位置已保留。");
                await ReplaceContentAsync(job, full.ToString(), force: true, cancellationToken);
            }
            else
            {
                await AddThinkingAsync(job, originalRequest, "正在生成配图。", cancellationToken);
                var articleWithoutImages = full.ToString();
                var images = new List<GeneratedArticleImage>();
                await foreach (var image in imageGenerationService.GenerateArticleImagesStreamAsync(
                    provider,
                    imageModel,
                    enrichedRequest.Content,
                    articleWithoutImages,
                    originalRequest.Options,
                    cancellationToken))
                {
                    var existingIndex = images.FindIndex(x => string.Equals(x.Title, image.Title, StringComparison.OrdinalIgnoreCase));
                    if (existingIndex >= 0) images[existingIndex] = image;
                    else images.Add(image);

                    var partialArticle = ArticleMarkdownImageComposer.TrimMarkdownTitle(
                        ArticleMarkdownImageComposer.PlaceImagesInArticlePreview(articleWithoutImages, images),
                        30);
                    await ReplaceContentAsync(job, partialArticle, force: false, cancellationToken);
                    await AddThinkingAsync(job, originalRequest, string.IsNullOrWhiteSpace(image.Url)
                        ? $"{image.Title} 生成失败：{image.Error}"
                        : image.IsPartial ? $"{image.Title} 正在生成，已收到预览图。" : $"{image.Title} 已生成并放入文章。", cancellationToken);
                }

                full.Clear().Append(ArticleMarkdownImageComposer.TrimMarkdownTitle(
                    ArticleMarkdownImageComposer.PlaceImagesInArticle(articleWithoutImages, images),
                    30));
                await ReplaceContentAsync(job, full.ToString(), force: true, cancellationToken);
                await AddThinkingAsync(job, originalRequest, BuildImageThinking(images), cancellationToken);
            }
        }

        var finalContent = ArticleMarkdownImageComposer.TrimMarkdownTitle(full.ToString(), 30);
        await ReplaceContentAsync(job, finalContent, force: true, cancellationToken);
        await AddThinkingAsync(job, originalRequest, "内容生成完成，已保存到会话历史。", cancellationToken);
        await CompleteAsync(job, cancellationToken);
    }

    private async Task HandleDirectImageGenerationAsync(
        GenerationJob job,
        SendMessageRequest originalRequest,
        ConversationIntent intent,
        AiProvider? provider,
        AiModel? imageModel,
        CancellationToken cancellationToken)
    {
        await AddThinkingAsync(job, originalRequest, imageModel is null
            ? "还没有配置可用的图片生成能力。"
            : "识别为直接生图请求，开始准备图片生成。", cancellationToken);

        if (imageModel is null)
        {
            await ReplaceContentAsync(job, "> 当前尚未配置生图模型，请先在设置里配置可用的图片模型。", force: true, cancellationToken);
            await CompleteAsync(job, cancellationToken);
            return;
        }

        var title = intent.Title;
        await ReplaceContentAsync(job, $"正在生成图片：{title}\n\n", force: true, cancellationToken);
        GeneratedArticleImage? generated = null;
        await foreach (var image in imageGenerationService.GenerateFromPromptStreamAsync(provider, imageModel, title, intent.Prompt, cancellationToken))
        {
            generated = image;
            await ReplaceContentAsync(job, BuildDirectImageMessage(image), force: true, cancellationToken);
            await AddThinkingAsync(job, originalRequest, string.IsNullOrWhiteSpace(image.Url)
                ? $"图片生成失败：{image.Error}"
                : image.IsPartial ? "图片正在生成，已收到预览图。" : "图片已生成。", cancellationToken);
        }

        await ReplaceContentAsync(job, generated is null
            ? "> 图片生成失败：生图接口没有返回结果。"
            : BuildDirectImageMessage(generated), force: true, cancellationToken);
        await AddThinkingAsync(job, originalRequest, "图片生成完成，已保存到会话历史。", cancellationToken);
        await CompleteAsync(job, cancellationToken);
    }

    private async Task<SendMessageRequest?> TryEnrichRequestWithWebPagesAsync(
        GenerationJob job,
        SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        var urls = webPageContentService.ExtractUrls(request.Content);
        if (urls.Count == 0)
        {
            return request;
        }

        await AddThinkingAsync(job, request, $"检测到 {urls.Count} 个链接，正在读取网页正文。", cancellationToken);
        var results = await webPageContentService.FetchAsync(urls, cancellationToken);
        var pages = results.Where(x => x.Page is not null).Select(x => x.Page!).ToList();
        var failures = results.Where(x => x.Page is null).ToList();

        foreach (var page in pages)
        {
            await AddThinkingAsync(job, request, $"已读取链接：{page.Title ?? page.Url}。", cancellationToken);
        }

        if (pages.Count == 0)
        {
            await ReplaceContentAsync(job, BuildWebPageFailureMessage(failures), force: true, cancellationToken);
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

    private async Task<IReadOnlyList<ChatTurn>> BuildTurnsForIntentAsync(
        long userId,
        long conversationId,
        SendMessageRequest request,
        ConversationIntent intent,
        CancellationToken cancellationToken)
    {
        var turns = await conversationService.BuildTurnsAsync(userId, conversationId, request, cancellationToken);
        var instruction = ConversationIntentCatalog.Get(intent.Type).ExecutionInstruction;
        var promptSettings = await appSettingsService.GetPromptSettingsAsync(cancellationToken);
        var adminInstruction = BuildAdminIntentPrompt(promptSettings, intent.Type);
        var mergedInstruction = string.Join(Environment.NewLine + Environment.NewLine, new[] { instruction, adminInstruction }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(mergedInstruction)
            ? turns
            : turns.Select((turn, index) => index == 0 && turn.Role == "system"
                ? new ChatTurn("system", turn.Content + Environment.NewLine + Environment.NewLine + mergedInstruction)
                : turn).ToList();
    }

    private static string BuildAdminIntentPrompt(PromptSettings settings, string? intentType)
    {
        var prompt = intentType?.Trim().ToLowerInvariant() switch
        {
            "article" or "article_image" => settings.Article,
            "document" or "summary" or "research" or "table" or "seo" or "script" or "social_post" or "code" or "translate" => settings.Document,
            "image" => settings.Image,
            "rewrite" => settings.Rewrite,
            "layout" => settings.Layout,
            "chat" => settings.Chat,
            _ => ""
        };

        return string.IsNullOrWhiteSpace(prompt)
            ? ""
            : "后台任务类型提示补充：\n" + prompt.Trim();
    }

    private async Task SetModeAsync(GenerationJob job, string? messageType, CancellationToken cancellationToken)
    {
        job.MessageType = string.IsNullOrWhiteSpace(messageType) ? null : messageType;
        if (job.AssistantMessage is not null)
        {
            job.AssistantMessage.MetadataJson = string.IsNullOrWhiteSpace(job.MessageType)
                ? "{}"
                : JsonSerializer.Serialize(new { messageType = job.MessageType }, JsonOptions);
        }

        await PersistAsync(job, force: true, cancellationToken);
    }

    private async Task ReplaceContentAsync(GenerationJob job, string content, bool force, CancellationToken cancellationToken)
    {
        job.Content = content;
        if (force && job.AssistantMessage is not null)
        {
            job.AssistantMessage.Content = string.IsNullOrWhiteSpace(content)
                ? "生成没有返回有效内容，请点击上一条消息重试。"
                : content;
            if (job.Conversation is not null)
            {
                job.Conversation.UpdatedAt = DateTime.UtcNow;
            }
        }

        await PersistAsync(job, force, cancellationToken);
    }

    private async Task AddThinkingAsync(GenerationJob job, SendMessageRequest request, string text, CancellationToken cancellationToken)
    {
        if (request.Options?.ShowThinking != true)
        {
            return;
        }

        var thinking = ReadThinking(job.ThinkingJson);
        thinking.Add(text);
        job.ThinkingJson = JsonSerializer.Serialize(thinking, JsonOptions);
        await PersistAsync(job, force: true, cancellationToken);
    }

    private async Task CompleteAsync(GenerationJob job, CancellationToken cancellationToken)
    {
        await ThrowIfCanceledAsync(job.Id, cancellationToken);
        if (string.IsNullOrWhiteSpace(job.Content))
        {
            await ReplaceContentAsync(job, "生成没有返回有效内容，请点击上一条消息重试。", force: true, cancellationToken);
        }

        job.Status = GenerationJobStatuses.Completed;
        job.CompletedAt = DateTime.UtcNow;
        await PersistAsync(job, force: true, cancellationToken);
    }

    private async Task PersistAsync(GenerationJob job, bool force, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (!force && now - lastPersistAt < MinStreamPersistInterval)
        {
            return;
        }

        await ThrowIfCanceledAsync(job.Id, force, cancellationToken);
        job.Version++;
        job.UpdatedAt = now;
        lastPersistAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ThrowIfCanceledAsync(long jobId, CancellationToken cancellationToken)
    {
        await ThrowIfCanceledAsync(jobId, force: true, cancellationToken);
    }

    private async Task ThrowIfCanceledAsync(long jobId, bool force, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (!force && now - lastCancelCheckAt < MinCancelCheckInterval)
        {
            return;
        }

        lastCancelCheckAt = now;
        var status = await db.GenerationJobs
            .AsNoTracking()
            .Where(x => x.Id == jobId)
            .Select(x => x.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (status == GenerationJobStatuses.Canceled)
        {
            throw new JobCanceledException();
        }
    }

    private static GenerationJobDto ToDto(GenerationJob job) =>
        new(job.Id, job.ConversationId, job.UserMessageId, job.AssistantMessageId, job.Status, job.Content,
            ReadThinking(job.ThinkingJson), job.MessageType, job.Error, job.Version, job.UpdatedAt);

    private static GenerationJobSummaryDto ToSummaryDto(GenerationJob job)
    {
        var request = JsonSerializer.Deserialize<SendMessageRequest>(job.RequestJson, JsonOptions);
        return new GenerationJobSummaryDto(
            job.Id,
            job.ConversationId,
            job.Conversation?.Title ?? "内容任务",
            job.UserMessageId,
            job.AssistantMessageId,
            job.Status,
            job.JobType,
            BuildPreview(request?.Content ?? ""),
            BuildPreview(job.Content),
            job.MessageType,
            job.Error,
            job.CreatedAt,
            job.UpdatedAt,
            job.StartedAt,
            job.CompletedAt);
    }

    private static string BuildPreview(string text)
    {
        var normalized = string.Join(" ", (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
    }

    private static List<string> ReadThinking(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool ShouldGenerateImages(ConversationIntent intent, SendMessageRequest request, string generatedContent)
    {
        if (intent.Type is not ("article" or "article_image"))
        {
            return false;
        }

        return ArticleMarkdownImageComposer.ContainsImagePlaceholders(generatedContent)
            || ArticleMarkdownImageComposer.ContainsImagePlaceholders(request.Content)
            || intent.Type == "article_image";
    }

    private static string BuildDirectImageMessage(GeneratedArticleImage image) =>
        !string.IsNullOrWhiteSpace(image.Url)
            ? ArticleMarkdownImageComposer.BuildInlineImageMarkdown(image)
            : $"> 图片生成失败：{image.Error}";

    private static string BuildIntentLabel(ConversationIntent intent) =>
        ConversationIntentCatalog.Get(intent.Type).Label;

    private static string BuildIntentConfirmationText(ConversationIntent intent) => $"""
        我还不太确定你这次想让我怎么处理。

        我当前判断是：{BuildIntentLabel(intent)}（置信度 {intent.Confidence:0.00}）。

        你可以直接补一句：
        - “按问答回答”
        - “写成文章”
        - “写成文档”
        - “直接生成图片”
        - “只给这篇文章配图，不要改正文”
        """;

    private static string BuildWebPageFailureMessage(IReadOnlyList<WebPageFetchResult> failures)
    {
        var builder = new StringBuilder();
        builder.AppendLine("我没有成功读取到链接正文，所以不能基于它生成内容，避免编造。");
        builder.AppendLine();
        builder.AppendLine("读取结果：");
        foreach (var failure in failures)
        {
            builder.AppendLine($"- {failure.Url}：{failure.Error}");
        }
        builder.AppendLine();
        builder.AppendLine("你可以稍后重试，或者把网页正文复制进来，我就能继续处理。");
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

    private static string BuildTitle(string message)
    {
        var normalized = message.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 28 ? normalized : normalized[..28] + "...";
    }
}

public sealed class JobCanceledException : Exception;

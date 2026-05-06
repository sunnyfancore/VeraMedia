using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public sealed class ConversationService(AppDbContext db, IAppSettingsService appSettingsService) : IConversationService
{
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(long userId, CancellationToken cancellationToken)
    {
        return await db.Conversations
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(80)
            .Select(x => new ConversationSummary(x.Id, x.Title, x.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MessageDto>> MessagesAsync(long userId, long conversationId, CancellationToken cancellationToken)
    {
        var exists = await db.Conversations.AnyAsync(x => x.Id == conversationId && x.UserId == userId, cancellationToken);
        if (!exists)
        {
            throw new InvalidOperationException("会话不存在。");
        }

        var messages = await db.ConversationMessages
            .Where(x => x.ConversationId == conversationId && !(x.Role == "assistant" && x.Content == ""))
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Role, x.Content, x.CreatedAt, x.MetadataJson })
            .ToListAsync(cancellationToken);

        return messages
            .Select(x => new MessageDto(x.Id, x.Role, x.Content, x.CreatedAt, ReadMessageType(x.MetadataJson)))
            .ToList();
    }

    public async Task<long> EnsureConversationAsync(long userId, long? conversationId, string firstMessage, CancellationToken cancellationToken)
    {
        Conversation conversation;
        if (conversationId is > 0)
        {
            conversation = await db.Conversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == userId, cancellationToken)
                ?? throw new InvalidOperationException("会话不存在。");
        }
        else
        {
            conversation = new Conversation
            {
                UserId = userId,
                Title = BuildTitle(firstMessage)
            };
            db.Conversations.Add(conversation);
        }

        conversation.UpdatedAt = DateTime.UtcNow;
        db.ConversationMessages.Add(new ConversationMessage
        {
            Conversation = conversation,
            Role = "user",
            Content = firstMessage
        });
        await db.SaveChangesAsync(cancellationToken);
        return conversation.Id;
    }

    public async Task AddAssistantMessageAsync(long userId, long conversationId, string content, CancellationToken cancellationToken, string? messageType = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            content = "生成没有返回有效内容，请点击上一条消息重试。";
        }

        var conversation = await db.Conversations.FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException("会话不存在。");

        conversation.UpdatedAt = DateTime.UtcNow;
        db.ConversationMessages.Add(new ConversationMessage
        {
            ConversationId = conversationId,
            Role = "assistant",
            Content = content,
            MetadataJson = string.IsNullOrWhiteSpace(messageType)
                ? "{}"
                : JsonSerializer.Serialize(new { messageType })
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatTurn>> BuildTurnsAsync(long userId, long conversationId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        var exists = await db.Conversations.AnyAsync(x => x.Id == conversationId && x.UserId == userId, cancellationToken);
        if (!exists)
        {
            throw new InvalidOperationException("会话不存在。");
        }

        var recent = await db.ConversationMessages
            .Where(x => x.ConversationId == conversationId && !(x.Role == "assistant" && x.Content == ""))
            .OrderByDescending(x => x.Id)
            .Take(16)
            .OrderBy(x => x.Id)
            .Select(x => new ChatTurn(x.Role, x.Content))
            .ToListAsync(cancellationToken);

        if (recent.Count > 0 && recent[^1].Role == "user")
        {
            recent[^1] = new ChatTurn("user", BuildUserContent(request));
        }

        var promptSettings = await appSettingsService.GetPromptSettingsAsync(cancellationToken);
        recent.Insert(0, new ChatTurn("system", string.Join(Environment.NewLine + Environment.NewLine, [
            BuildWorkflowGuardPrompt(request),
            BuildCreativeVariationPrompt(),
            BuildStylePresetPrompt(request.Options),
            BuildSystemPrompt(request.Options),
            BuildAdminPrompt(promptSettings)
        ])));
        return recent;
    }

    private static string BuildAdminPrompt(PromptSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.Global)
            ? ""
            : "后台全局提示补充：\n" + settings.Global.Trim();
    }

    private static string BuildStylePresetPrompt(AgentOptionsDto? options)
    {
        var preset = options?.StylePreset?.Trim().ToLowerInvariant();
        return preset switch
        {
            "story" => "风格预设：故事感。优先用场景、人物、冲突和细节开篇，正文保持叙事推进，避免空泛说教。",
            "practical" => "风格预设：干货型。优先输出可执行步骤、清单、方法和判断标准，减少情绪化表达。",
            "sharp" => "风格预设：犀利观点。允许更明确的判断、反差和观点密度，但不要低质夸张或制造焦虑。",
            "warm" => "风格预设：温暖陪伴。语言更柔和、有共情感，适合解释、复盘、教育和用户关系维护。",
            "conversion" => "风格预设：转化导向。突出痛点、价值、证据、行动建议和下一步，但不要硬广和过度承诺。",
            _ => "风格预设：均衡自然。表达清晰、有观点，但不过度模板化。"
        };
    }

    private static string BuildWorkflowGuardPrompt(SendMessageRequest request)
    {
        return string.Join(Environment.NewLine, [
            "高优先级工作流规则：",
            "- 先理解用户这次是在问答、写文章、写文档、直接生图，还是给已有文章配图；不要把普通问题自动改造成文章。",
            "- 如果用户要求“只配图、仅配图、只加图片、不要改正文、保留原文配图”，必须保留用户提供的文章原文，不要改写、扩写、重排观点或重新创作。",
            "- 只配图任务应在原文最合适的位置插入 {{image:封面图}}、{{image:正文配图 1}} 等图片占位符；除图片占位符外，正文内容尽量逐字保持不变。",
            "- 如果用户明确要求同时“优化/改写/润色/排版并配图”，才可以修改正文；修改幅度必须服从用户指令。",
            "- 如果用户要求直接生成图片，不要输出文章正文，只输出图片需求相关的简短响应。",
            $"- 本次用户原始需求：{request.Content}"
        ]);
    }

    private static string BuildCreativeVariationPrompt()
    {
        var angles = new[]
        {
            "从用户最容易忽略的反常识细节切入",
            "从一个具体场景或人物处境切入",
            "从行业变化和长期趋势切入",
            "从读者痛点和现实困境切入",
            "从数据、案例或事实冲突切入",
            "从一个尖锐问题或误区澄清切入",
            "从结果反推原因，先给结论再展开",
            "从对比关系切入，突出前后变化或两种选择"
        };
        var titleStrategies = new[]
        {
            "标题用悬念式，但不要夸张标题党",
            "标题用观点式，直接给出鲜明判断",
            "标题用问题式，引发读者继续阅读",
            "标题用对比式，体现冲突或反差",
            "标题用结果式，突出读者能获得什么",
            "标题用场景式，让读者一眼看到具体画面",
            "标题用提醒式，指出常见误区或风险",
            "标题用新知式，突出一个新发现或新理解"
        };
        var openingStyles = new[]
        {
            "开篇先写一个具体场景，再引出观点",
            "开篇先提出问题，再给出判断",
            "开篇先给反常识结论，再解释原因",
            "开篇先写读者熟悉的痛点，再转入主题",
            "开篇先用短句制造节奏，再展开背景",
            "开篇先引用一个事实或现象，再拔高到观点"
        };
        var structures = new[]
        {
            "结构采用“现象-原因-影响-建议”",
            "结构采用“痛点-误区-方法-行动”",
            "结构采用“故事-观点-拆解-总结”",
            "结构采用“问题-对比-答案-延展”",
            "结构采用“结论先行-分层论证-落地建议”",
            "结构采用“旧认知-新变化-新机会-提醒”"
        };

        return string.Join(Environment.NewLine, [
            "文章创作变体规则（仅在用户明确要求写作、改写、成文时使用）：",
            $"- 切入角度：{Pick(angles)}。",
            $"- 标题策略：{Pick(titleStrategies)}。",
            $"- 开篇方式：{Pick(openingStyles)}。",
            $"- 结构节奏：{Pick(structures)}。",
            "- 不要复用上一轮或常见模板化标题句式；同一主题也要换一种表达、换一组小标题、换一个叙述顺序。",
            "- 避免高频套话标题，例如“看完这篇你懂了”“背后的真相”“值得每个人看看”“一文讲透”，除非用户明确要求这种风格。"
        ]);
    }

    private static string Pick(IReadOnlyList<string> values)
    {
        return values[Random.Shared.Next(values.Count)];
    }

    private static string BuildSystemPrompt(AgentOptionsDto? options)
    {
        options ??= new AgentOptionsDto("normal", "wechat", "article", 0.7m, 3, false, true);
        return string.Join(Environment.NewLine, [
            "你是 VeraMedia 的中文内容运营助手。你可以聊天答疑、理解需求、策划选题，也可以在用户明确需要时创作文章、文档或配图占位。",
            "",
            "意图执行规则：",
            "- 普通问答、功能确认、问题排查、概念解释：直接用简洁自然的中文回答，不套文章模板，不输出图片占位符。",
            "- 文档、方案、报告、说明、清单、表格、邮件、计划、总结、SOP、PRD：使用自然 Markdown 流式输出，不使用文章卡片结构，不插入图片占位符，除非用户明确要求。",
            "- 文章、推文、公众号图文、小红书图文、新闻稿、软文、改写成文：输出完整 Markdown 文章。",
            "- 只配图、仅配图、不要改正文：保留原文，只插入图片占位符。",
            "- 用户意图不清时，先用 1-3 句话确认方向或给出可选切入角度，不要直接生成长文。",
            "",
            "文章创作原则：",
            "- 不照搬参考素材原文，必须用自己的语言重组表达，提炼核心观点并加入独立角度。",
            "- 标题要有吸引力和悬念感，可以使用数字、疑问、对比、反转等技巧，但不要低质夸张。",
            "- 开篇第一段要抓住注意力，可以用故事、场景、痛点、数据或反常识判断切入。",
            "- 每个小标题都要有观点和态度，不要使用“背景介绍”“总结”这类平淡标题。",
            "- 正文要有节奏感，长短句交替，适当使用排比、类比、反问等修辞。",
            "- 每次创作都要有不同的切入角度和表达方式，避免重复。",
            "",
            "文章输出格式：",
            "# 文章标题：15-25 字，有吸引力和悬念感",
            "",
            "{{image:封面图}}",
            "",
            "开篇段落（用故事、场景或痛点切入，抓住注意力）。",
            "",
            "## 小标题（有观点和态度，10-18 字）",
            "正文段落。",
            "",
            "{{image:正文配图 1}}",
            "",
            "## 小标题",
            "正文段落。",
            "",
            "{{image:正文配图 2}}",
            "",
            "如需要更多正文配图，继续使用 {{image:正文配图 3}}、{{image:正文配图 4}}。",
            "",
            "文章格式规则：",
            "- 最终文章只包含标题、正文和图片占位符，不输出分析过程、任务参数或额外解释。",
            "- 文章根据主题复杂度使用 3-6 个二级小标题，结构清晰但不要每次固定同一段落数量。",
            "- 封面图占位符放在标题后、正文前。",
            "- 正文配图占位符放在最匹配的段落后面，不要集中放到末尾；正文配图数量可根据内容自然选择 1-4 张。",
            "- 如果系统提供了网页正文或参考素材，必须深度改写和二次创作，提炼核心观点后用全新的表达方式呈现。",
            $"- 当前思考模式：{options.ThinkingMode}。"
        ]);
    }

    private static string BuildUserContent(SendMessageRequest request)
    {
        var builder = new StringBuilder(request.Content);
        if (request.Attachments is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("本次消息附件：");
            foreach (var file in request.Attachments)
            {
                builder.AppendLine($"- {file.FileName} ({file.ContentType}, {file.Size} bytes): {file.Url}");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Options?.Capability))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine($"能力模式：{request.Options.Capability}");
            if (request.Options.CapabilityParams is { Count: > 0 })
            {
                builder.AppendLine("能力参数：");
                foreach (var item in request.Options.CapabilityParams)
                {
                    builder.AppendLine($"- {item.Key}: {item.Value}");
                }
            }
            builder.AppendLine("请严格按照当前能力模式和能力参数处理用户需求。");
        }

        return builder.ToString();
    }

    private static string BuildTitle(string message)
    {
        var normalized = message.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 28 ? normalized : normalized[..28] + "...";
    }

    private static string? ReadMessageType(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || metadataJson == "{}")
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.TryGetProperty("messageType", out var value) ? value.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}

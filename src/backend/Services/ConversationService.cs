using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public sealed class ConversationService(AppDbContext db, IAppSettingsService appSettingsService) : IConversationService
{
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(long userId, CancellationToken cancellationToken, int page = 1, int pageSize = 80)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        return await db.Conversations
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
            .Select(x => new ChatTurn(x.Role, x.Content, null))
            .ToListAsync(cancellationToken);

        if (recent.Count > 0 && recent[^1].Role == "user")
        {
            recent[^1] = new ChatTurn("user", BuildUserContent(request), request.Attachments);
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
        options ??= new AgentOptionsDto("quick", "wechat", "article", 0.7m, 3, false, true);
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
            "# 这里写 15-25 字的文章标题，有吸引力和悬念感",
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
            var capabilityParams = request.Options.CapabilityParams ?? new Dictionary<string, string>();
            if (string.Equals(request.Options.Capability, "ppt", StringComparison.OrdinalIgnoreCase))
            {
                capabilityParams = capabilityParams
                    .Where(x => !x.Key.StartsWith("ppt", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(x => x.Key, x => x.Value);
            }

            if (capabilityParams.Count > 0)
            {
                builder.AppendLine("能力参数：");
                foreach (var item in capabilityParams)
                {
                    builder.AppendLine($"- {item.Key}: {item.Value}");
                }
            }

            var capabilityPrompt = BuildCapabilityExecutionPrompt(request.Options);
            if (!string.IsNullOrWhiteSpace(capabilityPrompt))
            {
                builder.AppendLine();
                builder.AppendLine(capabilityPrompt);
            }

            if (string.Equals(request.Options.Capability, "ppt", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine();
                builder.AppendLine("PPT 生成要求：");
                builder.AppendLine("- 不预设受众、风格、页数、结构和页面类型；请完全根据用户输入、附件内容和业务目标自行判断。");
                builder.AppendLine("- 这是一份成品级、有视觉主张的 PPT 制作任务，不是普通大纲整理。");
                builder.AppendLine("- 这不是普通 Markdown 文档导出，请按[PPT 制作]方式规划页面。");
                builder.AppendLine("- 必须输出一个 fenced code block，语言标记为 ppt-spec，内容是严格 JSON。不要把 JSON 当作给用户阅读的正文，前端会渲染为 PPT 制作卡片。");
                builder.AppendLine("```ppt-spec");
                builder.AppendLine("{\"title\":\"整套 PPT 标题\",\"subtitle\":\"一句话副标题\",\"audience\":\"受众\",\"theme\":\"自定义高级主题名\",\"design\":{\"style\":\"整体视觉风格与质感\",\"palette\":\"色彩、光感、材质建议\",\"motif\":\"贯穿全稿的视觉母题\",\"composition\":\"版式语言与画面节奏\"},\"slides\":[{\"title\":\"页标题\",\"subtitle\":\"可选副标题\",\"layout\":\"cover/agenda/title-content/two-column/section/summary/data-card/process/timeline/quote/stats 或语义化变体\",\"bullets\":[\"页面短要点1\",\"页面短要点2\"],\"visual\":\"这一页具体如何画：主视觉、图表、卡片、流程、对比矩阵、场景图、视觉隐喻、动线和留白\",\"imageUrl\":\"如果有可用的图片 URL 则填入，系统将自动嵌入该图片到幻灯片中\",\"notes\":\"演讲备注或旁白\"}]}");
                builder.AppendLine("```");
                builder.AppendLine("- JSON 外最多给一句简短说明，不要输出长篇 Markdown 大纲。");
                builder.AppendLine("- 你是在[担任创意总监制作 PPT]，不是[填模板]：先判断内容最有价值的叙事角度，再决定视觉风格、页面节奏、重点图形和信息层级。");
                builder.AppendLine();
                builder.AppendLine("## 创意方向");
                builder.AppendLine("- 每次都要重新定制：自行判断受众、叙事角度、视觉风格、页面数量、节奏和表达形态，不要沿用固定预设。");
                builder.AppendLine("- 不要照抄固定结构模板。根据材料选择最有冲击力的叙事方式，可以是问题洞察、未来愿景、产品发布、咨询报告、故事线、战役提案或数据叙事。");
                builder.AppendLine("- 封面必须有明确主张，不要只写项目名；副标题要像高端提案的定位语。");
                builder.AppendLine("- 可以使用 agenda，但如果会削弱高级感，可以用更自然的章节引导页、场景页或问题页替代。");
                builder.AppendLine("- layout 是渲染提示，不是创意边界。优先使用 cover, section, agenda, title-content, two-column, summary, data-card, process, timeline, quote, stats，也可以写语义化变体，但 visual 必须说明真实版式。");
                builder.AppendLine();
                builder.AppendLine("## 版式节奏");
                builder.AppendLine("- 避免一页页罗列。每 2-3 页要有节奏变化：大标题页、视觉冲击页、数据页、结构页、结论页交替出现。");
                builder.AppendLine("- 允许大胆留白、强对比、大数字、全幅场景、杂志式标题、咨询级框架图、发布会式视觉焦点。");
                builder.AppendLine("- 不要机械要求每份 PPT 都有 5 种 layout，也不要输出固定顺序模板；该克制时克制，该高能时高能。");
                builder.AppendLine();
                builder.AppendLine("## 内容质量");
                builder.AppendLine("- 每页只表达一个核心判断。标题要有观点和气势，不要写成目录项；bullets 写结论、数据、行动项或关键证据，不写描述性长句。");
                builder.AppendLine("- bullets 是页面可见正文，每页必须给 2-5 条，不能留空；visual 只作为渲染 brief，不要把需要展示的正文放进 visual。");
                builder.AppendLine("- bullets 禁止写排版指令，例如“右侧加结论卡片”“对比表用绿色勾选”“使用某某图标”；这类内容只能放在 visual，bullets 必须是最终 PPT 上能直接给客户看的业务内容。");
                builder.AppendLine("- 高端感来自取舍：减少废话，突出关键数字、矛盾、机会、路径和结果。不要泛泛发挥，也不要把资料简单搬运。");
                builder.AppendLine("- visual 字段必须像设计 brief：说明画面主体、构图、色彩、层级、图形模块、留白和情绪，而不只是'流程图'或'配图'。");
                builder.AppendLine("- imageUrl 字段：如果用户在对话中上传了图片或之前生成了图片，请将对应的图片 URL 填入相关页面的 imageUrl 字段。系统将自动下载并嵌入到幻灯片中，实现图文结合效果。");
                builder.AppendLine("- 每页 notes 都要写成可直接放入 PPT 备注区的演讲稿，和该页 bullets 一一对应，便于后续 PPT 转视频时音画同步。");
                builder.AppendLine("- 如果用户提供资料，先提炼资料中的事实、数字、卖点和结构，再制作 PPT 页面，不要泛泛发挥。");
                builder.AppendLine("- 如果用户明确要求 PPT 视频，请自行规划视频节奏、开场、转场、收尾，并让 notes 成为可直接朗读的旁白稿；否则输出适合导出为 PPT 的页面规格。");
            }
            builder.AppendLine("请严格按照当前能力模式和能力参数处理用户需求。");
        }

        return builder.ToString();
    }

    private static string BuildCapabilityExecutionPrompt(AgentOptionsDto options)
    {
        var capability = options.Capability?.Trim().ToLowerInvariant();
        var parameters = options.CapabilityParams ?? new Dictionary<string, string>();
        string GetParam(string key, string fallback) => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

        return capability switch
        {
            "quick" => "快速模式执行要求：直接解决用户当前问题，回答要简洁、准确、可执行；不要强行扩写成文章或报告。",
            "write" => string.Join(Environment.NewLine, [
                "帮我写作执行要求：",
                $"- 写作类型：{GetParam("writingType", "公众号文章")}；篇幅：{GetParam("writingLength", "中等")}。",
                "- 输出完整可直接使用的中文内容，标题、结构、正文要完整。",
                "- 如果用户上传附件或提供链接，必须先提炼素材中的事实与观点，再进行二次创作。",
                "- 默认按内容运营场景处理，避免空泛套话，避免输出任务说明。"
            ]),
            "code" => string.Join(Environment.NewLine, [
                "编程模式执行要求：",
                $"- 语言偏好：{GetParam("codeLanguage", "自动识别")}；任务类型：{GetParam("codeTask", "生成/修复")}。",
                "- 优先给出原因判断、可执行步骤和必要代码。",
                "- 如果用户上传代码文件，必须基于附件内容分析，不要假设不存在的文件结构。",
                "- 代码块要标注语言；涉及命令时给出可复制命令。"
            ]),
            "translate" => string.Join(Environment.NewLine, [
                "翻译模式执行要求：",
                $"- 目标语言：{GetParam("targetLanguage", "英文")}；翻译风格：{GetParam("translateMode", "自然表达")}。",
                "- 只输出翻译结果，除非用户要求解释。",
                "- 保持原意、语气、格式和专有名词一致；必要时采用本地化自然表达。"
            ]),
            "research" => string.Join(Environment.NewLine, [
                "深入研究执行要求：",
                $"- 研究深度：{GetParam("researchDepth", "标准")}。",
                "- 输出结构化报告，区分事实、推断和建议。",
                "- 如果系统提供网页正文或附件内容，必须基于这些来源；不要伪造来源或数据。",
                "- 结尾给出可执行建议和风险/不确定性。"
            ]),
            "qa" => string.Join(Environment.NewLine, [
                "解题答疑执行要求：",
                $"- 答疑方式：{GetParam("qaMode", "逐步讲解")}。",
                "- 先判断题目类型，再分步骤讲解。",
                "- 对数学、代码、逻辑题要展示关键推导；最后给出明确答案。",
                "- 如果题图或附件无法读取，先说明需要用户补充题干，不要编造题目内容。"
            ]),
            "data" => string.Join(Environment.NewLine, [
                "数据分析执行要求：",
                $"- 输出偏好：{GetParam("dataOutput", "洞察+表格")}。",
                "- 优先基于上传 CSV/XLSX/JSON/TXT 等附件中提取到的数据分析。",
                "- 输出关键指标、异常点、趋势、结论和下一步建议；适合时使用 Markdown 表格。",
                "- 如果数据不足或解析失败，明确说明限制，并给出需要补充的数据字段。"
            ]),
            "super" => string.Join(Environment.NewLine, [
                "超能模式执行要求：",
                "- 先识别用户任务目标，再综合写作、分析、研究、代码或文档能力完成。",
                "- 对复杂任务给出阶段化结果；需要假设时明确标注。",
                "- 输出要可交付、可复用，避免只给泛泛建议。"
            ]),
            "ppt" => string.Join(Environment.NewLine, [
                "PPT 生成模式执行要求：",
                "- 不使用固定受众、固定风格、固定页数或固定模板；由 AI 根据用户需求和资料自行定制。",
                "- 内容必须适合直接制作 PPT：每页标题有观点、要点有取舍、表达精炼，并包含 layout、bullets、visual、notes 等页面制作信息。",
                "- 请像创意总监一样组织叙事和视觉，而不是套固定模板；允许高级、醒目、有品牌感的页面节奏。",
                "- 不要把 PPT 写成普通文章或 Markdown 大纲；必须输出 ppt-spec JSON 规格，后端会按规格生成 PPTX。",
                "- 如果生成 PPT 视频，要为每页规划可朗读备注/旁白，保证备注与页面内容同步。"
            ]),
            "image" => string.Join(Environment.NewLine, [
                "图像生成模式执行要求：",
                $"- 图片比例：{options.ImageRatio ?? "1:1"}；风格：{options.ImageStyle ?? "默认"}；模板：{options.ImageTemplate ?? "none"}。",
                "- 输出应触发直接生图，不要生成长篇文字说明。",
                "- 如果有参考图，只能作为视觉参考，仍要遵循用户文字需求。"
            ]),
            _ => ""
        };
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

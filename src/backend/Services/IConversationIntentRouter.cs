using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public interface IConversationIntentRouter
{
    Task<ConversationIntent> RouteAsync(
        SendMessageRequest request,
        AiProvider? provider,
        AiModel? chatModel,
        CancellationToken cancellationToken);
}

public sealed partial class ConversationIntentRouter(IAiChatClient chatClient) : IConversationIntentRouter
{
    public async Task<ConversationIntent> RouteAsync(
        SendMessageRequest request,
        AiProvider? provider,
        AiModel? chatModel,
        CancellationToken cancellationToken)
    {
        var text = request.Content.ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ConversationIntent("chat", "chat", "", text, 0.4);
        }

        var forced = BuildForcedIntent(request);
        if (forced is not null)
        {
            return forced;
        }

        if (provider is null || chatModel is null || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            return FallbackRoute(request);
        }

        try
        {
            var classified = await ClassifyWithModelAsync(request, provider, chatModel, cancellationToken);
            return classified ?? FallbackRoute(request);
        }
        catch
        {
            return FallbackRoute(request);
        }
    }

    private static ConversationIntent? BuildForcedIntent(SendMessageRequest request)
    {
        var type = NormalizeType(request.Options?.IntentMode);
        if (type is null)
        {
            return null;
        }

        var text = request.Content.ReplaceLineEndings("\n").Trim();
        return new ConversationIntent(
            type,
            GetRenderMode(type),
            BuildTitle(text, DefaultTitle(type)),
            type == "image" ? BuildImagePrompt(text) : text,
            1.0);
    }

    private async Task<ConversationIntent?> ClassifyWithModelAsync(
        SendMessageRequest request,
        AiProvider provider,
        AiModel chatModel,
        CancellationToken cancellationToken)
    {
        var userText = request.Content.ReplaceLineEndings("\n").Trim();
        var turns = new[]
        {
            new ChatTurn("system", BuildClassifierPrompt()),
            new ChatTurn("user", $"用户消息：\n{userText}")
        };

        var output = new StringBuilder();
        var options = new AgentOptionsDto("normal", "wechat", "document", 0m, 0, false, false);
        await foreach (var chunk in chatClient.StreamReplyAsync(provider, chatModel, turns, options, cancellationToken))
        {
            output.Append(chunk);
            if (output.Length > 4000)
            {
                break;
            }
        }

        return ParseIntentJson(output.ToString(), userText);
    }

    private static ConversationIntent? ParseIntentJson(string raw, string userText)
    {
        var json = JsonObjectRegex().Match(raw).Value;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = NormalizeType(root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null);
        if (type is null)
        {
            return null;
        }

        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        var prompt = root.TryGetProperty("prompt", out var promptElement) ? promptElement.GetString() : null;
        var confidence = root.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.TryGetDouble(out var value)
            ? Math.Clamp(value, 0, 1)
            : 0.82;

        return new ConversationIntent(
            type,
            GetRenderMode(type),
            string.IsNullOrWhiteSpace(title) ? BuildTitle(userText, DefaultTitle(type)) : BuildTitle(title, DefaultTitle(type)),
            string.IsNullOrWhiteSpace(prompt) ? userText : prompt,
            confidence);
    }

    private static ConversationIntent FallbackRoute(SendMessageRequest request)
    {
        var text = request.Content.ReplaceLineEndings("\n").Trim();
        var compact = Normalize(text);
        if (string.IsNullOrWhiteSpace(compact))
        {
            return new ConversationIntent("chat", "chat", "", text, 0.4);
        }

        if (LooksLikeQuestion(compact))
        {
            return BuildFallbackIntent("chat", text, 0.72);
        }

        if (HasAny(compact, "只配图", "仅配图", "不要改正文", "保留原文配图"))
        {
            return BuildFallbackIntent("article_image", text, 0.9);
        }

        if (HasAny(compact, "生成图片", "生成一张图", "生图", "画一张", "做一张图", "实景图", "效果图", "海报", "图标", "logo"))
        {
            return BuildFallbackIntent("image", text, 0.88);
        }

        if (HasAny(compact, "润色", "改写", "扩写", "缩写", "降重", "换个说法"))
        {
            return BuildFallbackIntent("rewrite", text, 0.8);
        }

        if (HasAny(compact, "翻译", "译成", "英文版", "中文翻译"))
        {
            return BuildFallbackIntent("translate", text, 0.8);
        }

        if (HasAny(compact, "表格", "对比表", "清单表"))
        {
            return BuildFallbackIntent("table", text, 0.8);
        }

        if (HasAny(compact, "写文章", "生成文章", "公众号", "小红书图文", "新闻稿", "软文"))
        {
            return BuildFallbackIntent("article", text, 0.84);
        }

        if (HasAny(compact, "方案", "报告", "文档", "计划", "总结", "sop", "prd"))
        {
            return BuildFallbackIntent("document", text, 0.78);
        }

        return BuildFallbackIntent("chat", text, 0.7);
    }

    private static string BuildClassifierPrompt()
    {
        var typeLines = string.Join(
            Environment.NewLine,
            ConversationIntentCatalog.All.Select(x => $"- {x.Type}：{x.ClassifierDescription}"));
        var allowedTypes = string.Join("|", ConversationIntentCatalog.All.Select(x => x.Type));

        return string.Join(Environment.NewLine, [
            "你是 VeraMedia 的任务意图分类器，只返回 JSON，不要解释。",
            "",
            "可选 type：",
            typeLines,
            "",
            "重要边界：",
            "1. “图片生成为什么不对”“能否支持流式生图”“配图方案怎么做”是 chat，不是 image。",
            "2. “写一篇文章并配图”是 article，不是 article_image。",
            "3. “这篇文章只配图/不要改正文/给下面文章配图”是 article_image。",
            "4. “帮我生成一张家庭聚餐实景图”是 image。",
            "5. “帮我写会议室管理方案”是 document，不是 article。",
            "6. “把这段润色一下”是 rewrite，不是 article。",
            "7. “整理成表格/做对比表”是 table。",
            "",
            "返回格式必须是单个 JSON 对象：",
            $"{{\"type\":\"{allowedTypes}\",\"title\":\"短标题\",\"prompt\":\"生图时的图片提示词或原用户需求\",\"confidence\":0.0}}"
        ]);
    }

    private static ConversationIntent BuildFallbackIntent(string type, string text, double confidence)
    {
        var definition = ConversationIntentCatalog.Get(type);
        return new ConversationIntent(
            definition.Type,
            definition.Mode,
            definition.Type == "image" ? BuildImageTitle(text) : BuildTitle(text, definition.DefaultTitle),
            definition.Type == "image" ? BuildImagePrompt(text) : text,
            confidence);
    }

    private static string? NormalizeType(string? type) =>
        ConversationIntentCatalog.TryGet(type, out var definition) ? definition.Type : null;

    private static string GetRenderMode(string type) => ConversationIntentCatalog.Get(type).Mode;

    private static string DefaultTitle(string type) => ConversationIntentCatalog.Get(type).DefaultTitle;

    private static bool LooksLikeQuestion(string text)
    {
        return HasAny(text, "为什么", "是不是", "是否", "怎么回事", "怎么办", "如何", "能否", "能不能", "会不会", "怎么做", "问题", "不对");
    }

    private static string BuildImagePrompt(string userRequest)
    {
        return $"""
            根据用户需求生成一张图片。
            用户需求：{userRequest}
            视觉要求：严格遵循用户指定的主体、风格、画面元素和氛围；画面清晰，有细节，有完整构图；不要出现可读文字、水印、品牌 Logo。
            """;
    }

    private static string BuildImageTitle(string userRequest)
    {
        var cleaned = PrefixCleanupRegex().Replace(userRequest.ReplaceLineEndings(" "), "").Trim();
        return BuildTitle(cleaned, "AI 生成图片");
    }

    private static string BuildTitle(string text, string fallback)
    {
        var normalized = WhitespaceRegex().Replace(text.ReplaceLineEndings(" "), " ").Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized.Length <= 24 ? normalized : normalized[..24] + "...";
    }

    private static string Normalize(string text) => WhitespaceRegex().Replace(text, "").ToLowerInvariant();

    private static bool HasAny(string text, params string[] values) => values.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\{[\s\S]*\}")]
    private static partial Regex JsonObjectRegex();

    [GeneratedRegex(@"^(请|麻烦|帮我|给我|我要|想要|生成|画|做|出|来)(一张|一幅|一个|个)?")]
    private static partial Regex PrefixCleanupRegex();
}

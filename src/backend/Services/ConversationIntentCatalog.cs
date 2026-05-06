namespace VeraMedia.Api.Services;

public sealed record ConversationIntentDefinition(
    string Type,
    string Mode,
    string DefaultTitle,
    string Label,
    string ClassifierDescription,
    string ExecutionInstruction);

public static class ConversationIntentCatalog
{
    private static readonly IReadOnlyList<ConversationIntentDefinition> Definitions =
    [
        new("chat", "chat", "对话", "普通对话", "普通问答、排查问题、确认功能、讨论怎么做、解释概念、闲聊。", ""),
        new("document", "document", "文档", "文档输出", "方案、报告、说明、清单、邮件、合同、计划、总结、SOP、PRD 等通用文档。", "当前任务类型为 document。请按用户要求输出文档、方案、报告、说明或清单，使用自然 Markdown 流式输出；不要使用文章卡片结构，不要插入图片占位符，除非用户明确要求。"),
        new("article", "article", "文章", "文章生成", "写文章、生成推文、公众号、小红书图文、新闻稿、软文、改写成完整文章。只有 article 才用文章卡片。", ""),
        new("article_image", "article", "文章配图", "文章配图", "给已有文章、正文或稿子只配图、插图、补图、配封面，或明确不要改正文。", ""),
        new("image", "image", "AI 生成图片", "直接生图", "直接生成独立图片、照片、海报、图标、Logo、壁纸、实景图、效果图。", ""),
        new("rewrite", "rewrite", "改写润色", "改写润色", "润色、改写、扩写、缩写、降重、换风格，但不是要生成完整新文章。", "当前任务类型为 rewrite。请只改写、润色、扩写、缩写或降重用户给出的内容；不要擅自改成完整文章，不要插入图片占位符。"),
        new("layout", "layout", "智能排版", "智能排版", "排版、整理 Markdown、公众号排版、小红书排版、格式优化。", "当前任务类型为 layout。请专注排版、格式整理和结构优化，尽量保留原意；不要重新创作一篇新文章，不要插入图片占位符，除非用户明确要求。"),
        new("summary", "summary", "摘要总结", "摘要总结", "摘要、总结、提炼要点、标题、导语提取、信息压缩。", "当前任务类型为 summary。请提炼摘要、要点、标题、导语或结论；输出要紧凑清晰，不要扩写成文章。"),
        new("translate", "translate", "翻译", "翻译改写", "翻译、多语言改写、本地化表达。", "当前任务类型为 translate。请按用户指定语言翻译或本地化表达，保持原意和语气；不要添加无关解释。"),
        new("seo", "seo", "SEO/GEO 优化", "SEO/GEO 优化", "SEO/GEO 优化、关键词规划、搜索标题、描述、AI 搜索推荐优化。", "当前任务类型为 seo。请围绕 SEO/GEO、关键词、标题、描述、搜索推荐和内容可发现性给出结构化建议或优化结果。"),
        new("script", "script", "脚本", "脚本创作", "短视频脚本、直播话术、口播稿、分镜脚本。", "当前任务类型为 script。请输出短视频脚本、口播稿、直播话术或分镜脚本，结构清晰，便于直接使用。"),
        new("social_post", "social_post", "社媒文案", "社媒文案", "朋友圈、微博、小红书笔记、短推文、社媒文案。", "当前任务类型为 social_post。请输出适合社媒发布的短内容，如朋友圈、微博、小红书笔记或短推文；语言要自然、有传播感。"),
        new("table", "table", "表格", "表格清单", "表格、对比表、清单表、Excel 风格结构化输出。", "当前任务类型为 table。请优先用 Markdown 表格、清单表或对比表输出；字段清晰，便于复制整理。"),
        new("research", "research", "资料调研", "资料调研", "资料调研、竞品分析、信息整理、调研框架。", "当前任务类型为 research。请做资料调研、竞品分析或信息整理；区分事实、推断和建议，不要伪造不确定信息。"),
        new("code", "code", "技术问答", "技术问答", "代码、技术问答、接口调试、程序错误分析。", "当前任务类型为 code。请按技术问答或代码调试方式回答，优先给出可执行步骤、原因分析和代码示例；不要改写成文章。")
    ];

    private static readonly IReadOnlyDictionary<string, ConversationIntentDefinition> ByType =
        Definitions.ToDictionary(x => x.Type, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ConversationIntentDefinition> All => Definitions;

    public static bool TryGet(string? type, out ConversationIntentDefinition definition)
    {
        type = type?.Trim();
        if (!string.IsNullOrWhiteSpace(type) && ByType.TryGetValue(type, out definition!))
        {
            return true;
        }

        definition = ByType["chat"];
        return false;
    }

    public static ConversationIntentDefinition Get(string type)
    {
        return TryGet(type, out var definition) ? definition : ByType["chat"];
    }
}

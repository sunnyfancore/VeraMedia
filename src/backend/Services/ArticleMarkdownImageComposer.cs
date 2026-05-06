using System.Text;
using System.Text.RegularExpressions;

namespace VeraMedia.Api.Services;

public static class ArticleMarkdownImageComposer
{
    public static string StripImagePromptComments(string markdown)
    {
        var clean = Regex.Replace(markdown, @"<!--\s*image\s*-\s*prompt:[\s\S]*?-->", "", RegexOptions.IgnoreCase);
        return Regex.Replace(clean, @"<!--\s*image\s*-\s*prompt:[^\n]*", "", RegexOptions.IgnoreCase);
    }

    public static bool ContainsImagePlaceholders(string text)
    {
        return Regex.IsMatch(text, @"\{\{image:[^}]+\}\}", RegexOptions.IgnoreCase);
    }

    public static string TrimMarkdownTitle(string markdown, int maxLength)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n').ToList();
        var titleIndex = lines.FindIndex(x => x.StartsWith("# ", StringComparison.Ordinal));
        if (titleIndex < 0)
        {
            return markdown;
        }

        var title = lines[titleIndex][2..].Trim();
        if (title.Length <= maxLength)
        {
            return markdown;
        }

        lines[titleIndex] = "# " + title[..maxLength].TrimEnd('，', '。', '：', ':', '-', ' ');
        return string.Join('\n', lines);
    }

    public static string PlaceImagesInArticle(string article, IReadOnlyList<GeneratedArticleImage> images)
    {
        if (images.Count == 0)
        {
            return RemoveImagePlaceholders(article);
        }

        var result = article;
        var placedAny = false;
        foreach (var image in images)
        {
            var markdown = BuildInlineImageMarkdown(image);
            var placeholder = "{{image:" + image.Title + "}}";
            if (result.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(placeholder, markdown, StringComparison.OrdinalIgnoreCase);
                placedAny = true;
            }
        }

        result = RemoveImagePlaceholders(result);
        return placedAny ? result : InsertImagesFallback(result, images);
    }

    public static string PlaceImagesInArticlePreview(string article, IReadOnlyList<GeneratedArticleImage> images)
    {
        if (images.Count == 0)
        {
            return article;
        }

        var result = article;
        var placedAny = false;
        foreach (var image in images)
        {
            var markdown = BuildInlineImageMarkdown(image);
            var placeholder = "{{image:" + image.Title + "}}";
            if (result.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(placeholder, markdown, StringComparison.OrdinalIgnoreCase);
                placedAny = true;
            }
        }

        if (!placedAny)
        {
            return InsertImagesFallback(result, images);
        }

        return Regex.Replace(
            result,
            @"\{\{image:([^}]+)\}\}",
            match => $"> 图片生成中：{match.Groups[1].Value}");
    }

    public static string BuildInlineImageMarkdown(GeneratedArticleImage image)
    {
        if (!string.IsNullOrWhiteSpace(image.Url))
        {
            return $"{BuildImagePromptComment(image.Prompt)}\n![{image.Title}]({image.Url})";
        }

        return $"> {image.Title} 生成失败：{image.Error}";
    }

    private static string BuildImagePromptComment(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "";
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt));
        return $"<!-- image-prompt:{encoded} -->";
    }

    private static string RemoveImagePlaceholders(string article)
    {
        return Regex.Replace(article, @"\{\{image:[^}]+\}\}", "").Trim();
    }

    private static string InsertImagesFallback(string article, IReadOnlyList<GeneratedArticleImage> images)
    {
        var lines = article.ReplaceLineEndings("\n").Split('\n').ToList();
        var cover = images.FirstOrDefault(x => x.Title == "封面图");
        if (cover is not null)
        {
            var insertAt = lines.FindIndex(x => x.StartsWith("# "));
            lines.Insert(insertAt >= 0 ? insertAt + 1 : 0, "");
            lines.Insert(insertAt >= 0 ? insertAt + 2 : 1, BuildInlineImageMarkdown(cover));
        }

        var bodyImages = images.Where(x => x.Title.StartsWith("正文配图", StringComparison.OrdinalIgnoreCase)).ToList();
        if (bodyImages.Count == 0)
        {
            return string.Join('\n', lines);
        }

        var paragraphIndexes = lines
            .Select((line, index) => new { line, index })
            .Where(x => !string.IsNullOrWhiteSpace(x.line) && !x.line.StartsWith("#") && !x.line.StartsWith("!") && !x.line.StartsWith(">"))
            .Select(x => x.index)
            .ToList();

        for (var i = 0; i < bodyImages.Count; i++)
        {
            var target = paragraphIndexes.Count == 0
                ? lines.Count
                : paragraphIndexes[Math.Min(paragraphIndexes.Count - 1, Math.Max(0, (i + 1) * paragraphIndexes.Count / (bodyImages.Count + 1)))];
            lines.Insert(Math.Min(lines.Count, target + 1 + i * 2), "");
            lines.Insert(Math.Min(lines.Count, target + 2 + i * 2), BuildInlineImageMarkdown(bodyImages[i]));
        }

        return string.Join('\n', lines).Trim();
    }
}

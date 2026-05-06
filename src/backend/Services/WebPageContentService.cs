using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VeraMedia.Api.Services;

public sealed partial class WebPageContentService(HttpClient httpClient) : IWebPageContentService
{
    public IReadOnlyList<string> ExtractUrls(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return UrlRegex()
            .Matches(text)
            .Select(x => x.Value.TrimEnd('。', '，', ',', '.', ')', '）', ']', '】'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    public async Task<IReadOnlyList<WebPageFetchResult>> FetchAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        var results = new List<WebPageFetchResult>();
        foreach (var url in urls)
        {
            results.Add(await FetchOneAsync(url, cancellationToken));
        }

        return results;
    }

    private async Task<WebPageFetchResult> FetchOneAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 VeraMediaBot/1.0");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new WebPageFetchResult(url, null, $"读取失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                return new WebPageFetchResult(url, null, $"读取失败：暂不支持 {mediaType} 类型。");
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var title = ExtractTitle(html);
            var content = ExtractReadableText(html);
            if (content.Length < 120)
            {
                return new WebPageFetchResult(url, null, "读取失败：页面正文太少，可能需要登录、由脚本渲染，或被站点拦截。");
            }

            return new WebPageFetchResult(url, new WebPageContent(url, title, content), null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebPageFetchResult(url, null, "读取失败：请求网页超时。");
        }
        catch (Exception ex)
        {
            return new WebPageFetchResult(url, null, $"读取失败：{ex.Message}");
        }
    }

    private static string? ExtractTitle(string html)
    {
        var match = TitleRegex().Match(html);
        return match.Success ? CleanText(match.Groups[1].Value, 120) : null;
    }

    private static string ExtractReadableText(string html)
    {
        var text = ScriptRegex().Replace(html, " ");
        text = StyleRegex().Replace(text, " ");
        text = TagRegex().Replace(text, "\n");
        text = WebUtility.HtmlDecode(text);
        return CleanText(text, 12000);
    }

    private static string CleanText(string text, int maxLength)
    {
        var builder = new StringBuilder();
        foreach (var line in text.Replace("\r", "\n").Split('\n'))
        {
            var normalized = WhitespaceRegex().Replace(line, " ").Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            builder.AppendLine(normalized);
        }

        var result = builder.ToString().Trim();
        return result.Length <= maxLength ? result : result[..maxLength];
    }

    [GeneratedRegex(@"https?://[^\s<>'""，。；、）\]\}]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<script\b[^<]*(?:(?!</script>)<[^<]*)*</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<style\b[^<]*(?:(?!</style>)<[^<]*)*</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

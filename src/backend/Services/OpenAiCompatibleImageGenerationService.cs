using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public sealed class OpenAiCompatibleImageGenerationService(
    HttpClient httpClient,
    IWebHostEnvironment environment,
    IHttpContextAccessor httpContextAccessor) : IImageGenerationService
{
    private const int StreamPartialImageCount = 3;
    private const int MaxArticleImageCount = 6;
    private const string ImageGenerationPath = "/images/generations";
    private const string DefaultImageSize = "1024x1024";
    private static readonly TimeSpan ImageRequestTimeout = TimeSpan.FromSeconds(600);

    public async Task<IReadOnlyList<GeneratedArticleImage>> GenerateArticleImagesAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string userRequest,
        string articleMarkdown,
        AgentOptionsDto? options,
        CancellationToken cancellationToken)
    {
        var results = new List<GeneratedArticleImage>();
        await foreach (var image in GenerateArticleImagesStreamAsync(
            provider,
            imageModel,
            userRequest,
            articleMarkdown,
            options,
            cancellationToken).WithCancellation(cancellationToken))
        {
            var existingIndex = results.FindIndex(x => string.Equals(x.Title, image.Title, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                results[existingIndex] = image;
            }
            else
            {
                results.Add(image);
            }
        }

        return results;
    }

    public async IAsyncEnumerable<GeneratedArticleImage> GenerateArticleImagesStreamAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string userRequest,
        string articleMarkdown,
        AgentOptionsDto? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (provider is null || imageModel is null || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            yield break;
        }

        foreach (var item in BuildImagePrompts(userRequest, articleMarkdown, options))
        {
            await foreach (var image in GeneratePromptImageStreamWithErrorsAsync(provider, imageModel, item.Title, item.Prompt, cancellationToken))
            {
                yield return image;
            }
        }
    }

    public async Task<GeneratedArticleImage> GenerateSingleArticleImageAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string title,
        string articleMarkdown,
        CancellationToken cancellationToken)
    {
        return await GetFinalImageAsync(
            GenerateSingleArticleImageStreamAsync(provider, imageModel, title, articleMarkdown, cancellationToken),
            new GeneratedArticleImage(title, "", null, "生图接口没有返回结果。"),
            cancellationToken);
    }

    public async IAsyncEnumerable<GeneratedArticleImage> GenerateSingleArticleImageStreamAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string title,
        string articleMarkdown,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (provider is null || imageModel is null || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            yield return new GeneratedArticleImage(title, "", null, "未配置可用的生图模型。");
            yield break;
        }

        var prompt = BuildSingleArticleImagePrompt(title, articleMarkdown);
        await foreach (var image in GeneratePromptImageStreamWithErrorsAsync(provider, imageModel, title, prompt, cancellationToken))
        {
            yield return image;
        }
    }

    public async Task<GeneratedArticleImage> GenerateFromPromptAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string title,
        string prompt,
        CancellationToken cancellationToken)
    {
        return await GetFinalImageAsync(
            GenerateFromPromptStreamAsync(provider, imageModel, title, prompt, cancellationToken),
            new GeneratedArticleImage(title, prompt, null, "生图接口没有返回结果。"),
            cancellationToken);
    }

    public async IAsyncEnumerable<GeneratedArticleImage> GenerateFromPromptStreamAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string title,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (provider is null || imageModel is null || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            yield return new GeneratedArticleImage(title, prompt, null, "未配置可用的生图模型。");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return new GeneratedArticleImage(title, prompt, null, "请先输入图片提示词。");
            yield break;
        }

        await foreach (var image in GeneratePromptImageStreamWithErrorsAsync(provider, imageModel, title, prompt, cancellationToken))
        {
            yield return image;
        }
    }

    private static async Task<GeneratedArticleImage> GetFinalImageAsync(
        IAsyncEnumerable<GeneratedArticleImage> images,
        GeneratedArticleImage fallback,
        CancellationToken cancellationToken)
    {
        var final = fallback;
        await foreach (var image in images.WithCancellation(cancellationToken))
        {
            final = image;
        }

        return final;
    }

    private async IAsyncEnumerable<GeneratedArticleImage> GeneratePromptImageStreamWithErrorsAsync(
        AiProvider provider,
        AiModel imageModel,
        string title,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ImageRequestTimeout);
        var enumerator = GenerateOneStreamAsync(provider, imageModel, title, prompt, timeout.Token)
            .GetAsyncEnumerator(cancellationToken);
        Exception? error = null;
        try
        {
            while (true)
            {
                GeneratedArticleImage image;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    image = enumerator.Current;
                }
                catch (Exception ex)
                {
                    error = ex;
                    break;
                }

                yield return image;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            yield return new GeneratedArticleImage(title, prompt, null, "生图请求超时，请稍后重试或换用更快的图片模型。");
        }
        else if (error is HttpRequestException httpError)
        {
            yield return new GeneratedArticleImage(title, prompt, null, BuildFriendlyError(httpError));
        }
        else if (error is not null)
        {
            yield return new GeneratedArticleImage(title, prompt, null, error.Message);
        }
    }

    private async IAsyncEnumerable<GeneratedArticleImage> GenerateOneStreamAsync(
        AiProvider provider,
        AiModel imageModel,
        string title,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = provider.BaseUrl.TrimEnd('/') + ImageGenerationPath;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyClientUserAgent(request);
        request.Content = JsonContent.Create(new
        {
            model = imageModel.Name,
            prompt,
            n = 1,
            size = DefaultImageSize,
            stream = true,
            partial_images = StreamPartialImageCount
        });

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
            throw new HttpRequestException(
                $"生图接口返回 {(int)response.StatusCode} {response.ReasonPhrase}。{detail}",
                null,
                response.StatusCode);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
            var url = await ExtractImageUrlAsync(doc.RootElement, cancellationToken);
            yield return string.IsNullOrWhiteSpace(url)
                ? new GeneratedArticleImage(title, prompt, null, "生图接口没有返回可用图片。")
                : new GeneratedArticleImage(title, prompt, url, null);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                var payload = data.ToString();
                data.Clear();
                var image = await TryReadImageEventAsync(title, prompt, payload, cancellationToken);
                if (image is not null)
                {
                    yield return image;
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[5..].Trim();
                if (value == "[DONE]")
                {
                    break;
                }

                data.AppendLine(value);
            }
        }
    }

    private async Task<string?> ExtractImageUrlAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (TryExtractImagePayload(root, out var url, out var b64, out _))
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            if (!string.IsNullOrWhiteSpace(b64))
            {
                return await SaveBase64ImageAsync(b64, cancellationToken);
            }
        }

        return null;
    }

    private async Task<GeneratedArticleImage?> TryReadImageEventAsync(
        string title,
        string prompt,
        string payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!TryExtractImagePayload(doc.RootElement, out var url, out var b64, out var isPartial))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(b64))
            {
                url = isPartial
                    ? $"data:image/png;base64,{b64}"
                    : await SaveBase64ImageAsync(b64, cancellationToken);
            }

            return string.IsNullOrWhiteSpace(url) ? null : new GeneratedArticleImage(title, prompt, url, null, isPartial);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryExtractImagePayload(JsonElement root, out string? url, out string? b64, out bool isPartial)
    {
        url = null;
        b64 = null;
        isPartial = false;

        if (root.TryGetProperty("type", out var typeElement))
        {
            var type = typeElement.GetString() ?? "";
            isPartial = type.Contains("partial", StringComparison.OrdinalIgnoreCase);
        }

        if (root.TryGetProperty("partial_image_index", out _))
        {
            isPartial = true;
        }

        if (root.TryGetProperty("url", out var urlElement))
        {
            url = urlElement.GetString();
        }

        if (root.TryGetProperty("b64_json", out var b64Element))
        {
            b64 = b64Element.GetString();
        }

        if ((string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(b64)) &&
            root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array &&
            data.GetArrayLength() > 0)
        {
            return TryExtractImagePayload(data[0], out url, out b64, out isPartial);
        }

        return !string.IsNullOrWhiteSpace(url) || !string.IsNullOrWhiteSpace(b64);
    }

    private async Task<string> SaveBase64ImageAsync(string base64, CancellationToken cancellationToken)
    {
        var uploadRoot = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "uploads");
        Directory.CreateDirectory(uploadRoot);

        var fileName = $"{Guid.NewGuid():N}.png";
        var fullPath = Path.Combine(uploadRoot, fileName);
        var bytes = Convert.FromBase64String(base64);
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);

        var request = httpContextAccessor.HttpContext?.Request;
        if (request is null)
        {
            return $"/uploads/{fileName}";
        }

        return $"{request.Scheme}://{request.Host}/uploads/{fileName}";
    }

    private static IReadOnlyList<(string Title, string Prompt)> BuildImagePrompts(
        string userRequest,
        string articleMarkdown,
        AgentOptionsDto? options)
    {
        var summary = BuildSummary(articleMarkdown);
        var placeholders = ExtractImagePlaceholders(articleMarkdown);
        if (placeholders.Count == 0)
        {
            var imageCount = Math.Clamp(options?.ImageCount ?? StreamPartialImageCount, 1, MaxArticleImageCount);
            placeholders = ["封面图", .. Enumerable.Range(1, imageCount).Select(x => $"正文配图 {x}")];
        }

        return placeholders.Select((title, index) =>
        {
            var context = ExtractContextAroundPlaceholder(articleMarkdown, "{{image:" + title + "}}");
            var isCover = title.Contains("封面", StringComparison.OrdinalIgnoreCase) || index == 0;
            var prompt = isCover
                ? $"""
                  为这篇中文图文文章重新创作封面图。
                  图片位置：{title}
                  用户需求：{userRequest}
                  文章摘要：{summary}
                  视觉要求：适合中文内容平台封面，主题明确，专业、有吸引力；不要出现可读文字、水印、品牌 Logo。
                  构图要求：主体清晰，留出标题排版空间，画面干净，商业质感。
                  """
                : $"""
                  为这篇中文图文文章重新创作正文配图。
                  图片位置：{title}
                  用户需求：{userRequest}
                  配图所在段落上下文：{context}
                  文章整体摘要：{summary}
                  视觉要求：与所在段落强相关，适合插入正文；不要出现可读文字、水印、品牌 Logo；画面清晰、真实、有信息感。
                  """;
            return (title, prompt);
        }).ToList();
    }

    private static string BuildSingleArticleImagePrompt(string title, string articleMarkdown)
    {
        return $"""
            为中文图文文章重新生成图片。
            图片位置：{title}
            文章上下文：{BuildSummary(articleMarkdown)}
            视觉要求：与文章内容强相关，适合插入正文；不要出现可读文字、水印、品牌 Logo；画面清晰、真实、有信息感。
            """;
    }

    private static List<string> ExtractImagePlaceholders(string markdown)
    {
        return System.Text.RegularExpressions.Regex.Matches(markdown, @"\{\{image:([^}]+)\}\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(x => x.Groups[1].Value.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static string ExtractContextAroundPlaceholder(string markdown, string placeholder)
    {
        var index = markdown.IndexOf(placeholder, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return BuildSummary(markdown);
        }

        var start = Math.Max(0, index - 500);
        var length = Math.Min(markdown.Length - start, 1000);
        return markdown.Substring(start, length).Replace(placeholder, " ");
    }

    private static string BuildFriendlyError(HttpRequestException ex)
    {
        var message = ex.Message;
        if ((int?)ex.StatusCode == 504 || message.Contains("504", StringComparison.OrdinalIgnoreCase))
        {
            return "生图服务网关超时。通常是图片模型生成太慢或服务商当前繁忙。";
        }

        if (ex.InnerException is TaskCanceledException || message.Contains("canceled", StringComparison.OrdinalIgnoreCase))
        {
            return "生图请求超时，请稍后重试或换用更快的图片模型。";
        }

        if ((int?)ex.StatusCode is 401 or 403)
        {
            return "生图接口鉴权失败，请检查 API Key 是否有图片模型权限。";
        }

        if ((int?)ex.StatusCode == 404)
        {
            return $"生图接口地址或模型不兼容，请确认 Base URL 是否支持 {ImageGenerationPath}。";
        }

        return message;
    }

    private void ApplyClientUserAgent(HttpRequestMessage request)
    {
        var clientUa = httpContextAccessor.HttpContext?.Request.Headers["X-Client-User-Agent"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(clientUa))
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", clientUa);
        }
    }

    private static string BuildSummary(string markdown)
    {
        var plain = markdown
            .Replace("#", " ")
            .Replace("*", " ")
            .Replace("`", " ")
            .ReplaceLineEndings(" ");
        return plain.Length <= 700 ? plain : plain[..700];
    }
}

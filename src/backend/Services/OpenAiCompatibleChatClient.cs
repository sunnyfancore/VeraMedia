using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public sealed class OpenAiCompatibleChatClient(HttpClient httpClient, IHttpContextAccessor httpContextAccessor) : IAiChatClient
{
    public async IAsyncEnumerable<string> StreamReplyAsync(
        AiProvider? provider,
        AiModel? model,
        IReadOnlyList<ChatTurn> turns,
        AgentOptionsDto? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (provider is null || model is null || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            await foreach (var chunk in StreamDemoReplyAsync(turns, cancellationToken))
            {
                yield return chunk;
            }

            yield break;
        }

        if (options?.EnableWebSearch == true)
        {
            await foreach (var chunk in StreamResponsesWithWebSearchAsync(provider, model, turns, options, cancellationToken))
            {
                yield return chunk;
            }

            yield break;
        }

        await foreach (var chunk in StreamChatCompletionsAsync(provider, model, turns, options, cancellationToken))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<string> StreamChatCompletionsAsync(
        AiProvider provider,
        AiModel model,
        IReadOnlyList<ChatTurn> turns,
        AgentOptionsDto? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = provider.BaseUrl.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        ApplyClientUserAgent(request);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model.Name,
            ["stream"] = true,
            ["temperature"] = (double)(options?.Temperature ?? 0.7m),
            ["messages"] = turns.Select(x => new { role = x.Role, content = x.Content }).ToArray()
        };
        if (options?.ThinkingMode == "deep")
        {
            payload["reasoning_effort"] = "high";
        }

        request.Content = JsonContent.Create(payload);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
            {
                yield break;
            }

            var delta = TryReadChatDelta(data);
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    private async IAsyncEnumerable<string> StreamResponsesWithWebSearchAsync(
        AiProvider provider,
        AiModel model,
        IReadOnlyList<ChatTurn> turns,
        AgentOptionsDto? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = provider.BaseUrl.TrimEnd('/') + "/responses";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        ApplyClientUserAgent(request);

        var input = turns.Select(x => new
        {
            role = x.Role,
            content = x.Content
        }).ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model.Name,
            ["stream"] = true,
            ["input"] = input,
            ["tools"] = new object[] { new { type = "web_search" } },
            ["tool_choice"] = "auto",
            ["temperature"] = (double)(options?.Temperature ?? 0.7m)
        };

        if (options?.ThinkingMode == "deep")
        {
            payload["reasoning"] = new
            {
                effort = "high"
            };
        }

        request.Content = JsonContent.Create(payload);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sources = new SortedDictionary<int, string>();

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            var delta = TryReadResponseDelta(data, sources);
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }

        if (sources.Count > 0)
        {
            yield return "\n\n---\n\n## 参考来源\n" + string.Join("\n", sources.Select(x => $"{x.Key}. {x.Value}"));
        }
    }

    private static string? TryReadChatDelta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta) &&
            delta.TryGetProperty("content", out var content))
        {
            return content.GetString();
        }

        if (choice.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var messageContent))
        {
            return messageContent.GetString();
        }

        return null;
    }

    private static string? TryReadResponseDelta(string json, IDictionary<int, string> sources)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        if (type is "response.output_text.delta" && root.TryGetProperty("delta", out var delta))
        {
            return delta.GetString();
        }

        if (type is "response.output_text.annotation.added")
        {
            TryAddSource(root, sources);
        }

        if (type is "response.completed" && root.TryGetProperty("response", out var response))
        {
            TryCollectSources(response, sources);
        }

        return null;
    }

    private static void TryCollectSources(JsonElement element, IDictionary<int, string> sources)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) &&
                type.GetString() == "url_citation" &&
                element.TryGetProperty("url", out _))
            {
                AddSource(element, sources);
            }

            foreach (var property in element.EnumerateObject())
            {
                TryCollectSources(property.Value, sources);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                TryCollectSources(item, sources);
            }
        }
    }

    private static void TryAddSource(JsonElement root, IDictionary<int, string> sources)
    {
        if (root.TryGetProperty("annotation", out var annotation))
        {
            AddSource(annotation, sources);
        }
    }

    private static void AddSource(JsonElement annotation, IDictionary<int, string> sources)
    {
        if (!annotation.TryGetProperty("url", out var urlElement))
        {
            return;
        }

        var url = urlElement.GetString();
        if (string.IsNullOrWhiteSpace(url) || sources.Values.Any(x => x.Contains(url, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var title = annotation.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        sources[sources.Count + 1] = string.IsNullOrWhiteSpace(title)
            ? url
            : $"[{title}]({url})";
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

    private static async IAsyncEnumerable<string> StreamDemoReplyAsync(
        IReadOnlyList<ChatTurn> turns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lastUser = turns.LastOrDefault(x => x.Role == "user")?.Content ?? "";
        var reply = $"""
        我已经收到你的内容需求。当前还没有配置可用的 OpenAI 兼容 Provider，所以这是演示回复。

        我会把这条消息当作一个内容运营任务来处理：
        1. 识别输入类型：问题、链接、文章、选题或修改指令。
        2. 根据意图选择问答、文章、文档、配图或直接生图链路。
        3. 需要配图时生成封面图和正文图提示词。
        4. 把结果保存在会话里，方便继续修改。

        你刚才的输入是：
        {lastUser}

        下一步可以在设置里配置 API：Base URL 例如 `https://api.openai.com/v1`，模型名填写兼容的聊天模型，然后我就会切到真实流式生成。
        """;

        foreach (var ch in reply.Chunk(16))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(35, cancellationToken);
            yield return new string(ch);
        }
    }
}

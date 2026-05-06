using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
[Route("api/providers")]
public sealed class ProvidersController(
    AppDbContext db,
    IHttpClientFactory httpClientFactory,
    IAuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProviderResponse>>> List(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var rows = await db.AiProviders
            .Where(x => x.UserId == userId)
            .Include(x => x.Models)
            .OrderByDescending(x => x.Enabled)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(AiProviderResponseMapper.ToResponse));
    }

    [HttpPost]
    public async Task<ActionResult<ProviderResponse>> Save(ProviderRequest request, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var name = string.IsNullOrWhiteSpace(request.Name) ? "OpenAI" : request.Name.Trim();
        var provider = await db.AiProviders
            .Include(x => x.Models)
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Name == name, cancellationToken);

        if (provider is null)
        {
            provider = new AiProvider { UserId = userId, Name = name, Enabled = true };
            db.AiProviders.Add(provider);
        }

        var otherProviders = await db.AiProviders
            .Where(x => x.UserId == userId && x.Id != provider.Id)
            .ToListAsync(cancellationToken);
        foreach (var other in otherProviders)
        {
            other.Enabled = false;
        }

        provider.BaseUrl = request.BaseUrl.Trim().TrimEnd('/');
        provider.Enabled = true;
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            provider.ApiKey = request.ApiKey.Trim();
        }

        AiProviderResponseMapper.UpsertModel(provider, AiModelTypes.Chat, request.ChatModelName);
        AiProviderResponseMapper.UpsertModel(provider, AiModelTypes.Image, request.ImageModelName);

        await db.SaveChangesAsync(cancellationToken);
        await auditLogger.LogAsync(userId, "save_provider", "provider", provider.Id.ToString(), $"保存 API 配置 {provider.Name}", cancellationToken);
        return Ok(AiProviderResponseMapper.ToResponse(provider));
    }

    [HttpPost("models")]
    public async Task<ActionResult<ModelListResponse>> ListModels(ModelListRequest request, CancellationToken cancellationToken)
    {
        var resolved = await ResolveConfigAsync(User.GetUserId(), request.Name, request.BaseUrl, request.ApiKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolved.BaseUrl) || string.IsNullOrWhiteSpace(resolved.ApiKey))
        {
            return BadRequest(new { message = "请先填写 Base URL，并保存或输入 API Key。" });
        }

        using var response = await SendProviderRequestAsync(HttpMethod.Get, $"{resolved.BaseUrl}/models", resolved.ApiKey, null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return BadRequest(new { message = $"模型列表获取失败：{(int)response.StatusCode} {response.ReasonPhrase}" });
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var models = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    models.Add(id.GetString()!);
                }
            }
        }

        return Ok(new ModelListResponse(models.OrderBy(x => x).ToList()));
    }

    [HttpPost("test")]
    public async Task<ActionResult<ProviderTestResponse>> Test(ProviderTestRequest request, CancellationToken cancellationToken)
    {
        var resolved = await ResolveConfigAsync(User.GetUserId(), request.Name, request.BaseUrl, request.ApiKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolved.BaseUrl) || string.IsNullOrWhiteSpace(resolved.ApiKey))
        {
            return BadRequest(new ProviderTestResponse(false, "请先填写 Base URL，并保存或输入 API Key。"));
        }

        try
        {
            var type = request.TestType.Trim().ToLowerInvariant();
            if (type == "image")
            {
                if (string.IsNullOrWhiteSpace(request.ImageModelName))
                {
                    return BadRequest(new ProviderTestResponse(false, "请先填写生图模型名。"));
                }

                return Ok(await TestImageAsync(resolved.BaseUrl, resolved.ApiKey, request.ImageModelName, cancellationToken));
            }

            if (string.IsNullOrWhiteSpace(request.ChatModelName))
            {
                return BadRequest(new ProviderTestResponse(false, "请先填写聊天模型名。"));
            }

            return Ok(await TestChatAsync(resolved.BaseUrl, resolved.ApiKey, request.ChatModelName, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return BadRequest(new ProviderTestResponse(false, "测试超时，请检查网络、Base URL 或服务商状态。"));
        }
        catch (Exception ex)
        {
            return BadRequest(new ProviderTestResponse(false, "测试失败。", ex.Message));
        }
    }

    private async Task<ProviderTestResponse> TestChatAsync(string baseUrl, string apiKey, string model, CancellationToken cancellationToken)
    {
        var payload = JsonContent.Create(new
        {
            model,
            stream = false,
            messages = new[] { new { role = "user", content = "ping" } },
            max_tokens = 8
        });
        using var response = await SendProviderRequestAsync(HttpMethod.Post, $"{baseUrl}/chat/completions", apiKey, payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? new ProviderTestResponse(true, "聊天模型连接正常。")
            : new ProviderTestResponse(false, $"聊天模型测试失败：{(int)response.StatusCode} {response.ReasonPhrase}", Truncate(body));
    }

    private async Task<ProviderTestResponse> TestImageAsync(string baseUrl, string apiKey, string model, CancellationToken cancellationToken)
    {
        var payload = JsonContent.Create(new
        {
            model,
            prompt = "a tiny blue circle on a white background",
            n = 1,
            size = "1024x1024"
        });
        using var response = await SendProviderRequestAsync(HttpMethod.Post, $"{baseUrl}/images/generations", apiKey, payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? new ProviderTestResponse(true, "生图模型连接正常。")
            : new ProviderTestResponse(false, $"生图模型测试失败：{(int)response.StatusCode} {response.ReasonPhrase}", Truncate(body));
    }

    private async Task<(string BaseUrl, string ApiKey)> ResolveConfigAsync(long userId, string name, string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        var resolvedBaseUrl = baseUrl.Trim().TrimEnd('/');
        var resolvedApiKey = apiKey.Trim();
        if (!string.IsNullOrWhiteSpace(resolvedApiKey) && !string.IsNullOrWhiteSpace(resolvedBaseUrl))
        {
            return (resolvedBaseUrl, resolvedApiKey);
        }

        var saved = await db.AiProviders
            .Where(x => x.UserId == userId && x.Enabled)
            .OrderByDescending(x => x.Name == name)
            .ThenByDescending(x => x.BaseUrl == resolvedBaseUrl)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (saved is null)
        {
            return (resolvedBaseUrl, resolvedApiKey);
        }

        return (
            string.IsNullOrWhiteSpace(resolvedBaseUrl) ? saved.BaseUrl.TrimEnd('/') : resolvedBaseUrl,
            string.IsNullOrWhiteSpace(resolvedApiKey) ? saved.ApiKey : resolvedApiKey);
    }

    private async Task<HttpResponseMessage> SendProviderRequestAsync(HttpMethod method, string url, string apiKey, HttpContent? content, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var message = new HttpRequestMessage(method, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        ApplyClientUserAgent(message);
        message.Content = content;
        return await client.SendAsync(message, timeout.Token);
    }

    private void ApplyClientUserAgent(HttpRequestMessage message)
    {
        var clientUa = Request.Headers["X-Client-User-Agent"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(clientUa))
        {
            message.Headers.UserAgent.ParseAdd("VeraMedia/1.0 (+https://localhost)");
        }
        else
        {
            message.Headers.TryAddWithoutValidation("User-Agent", clientUa);
        }
    }

    private static string Truncate(string value)
    {
        value = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length <= 500 ? value : value[..500] + "...";
    }
}

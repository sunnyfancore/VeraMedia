using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace VeraMedia.Api.Services;

public static class SseResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Prepare(HttpResponse response)
    {
        response.Headers.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    public static async Task WriteAsync(
        HttpResponse response,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await response.WriteAsync($"data: {JsonSerializer.Serialize(payload, JsonOptions)}\n", cancellationToken);
        await response.WriteAsync("\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

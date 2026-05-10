using VeraMedia.Api.Contracts;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public sealed record GeneratedArticleImage(string Title, string Prompt, string? Url, string? Error, bool IsPartial = false);

public interface IImageGenerationService
{
    Task<IReadOnlyList<GeneratedArticleImage>> GenerateArticleImagesAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string userRequest,
        string articleMarkdown,
        AgentOptionsDto? options,
        CancellationToken cancellationToken);

    IAsyncEnumerable<GeneratedArticleImage> GenerateArticleImagesStreamAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string userRequest,
        string articleMarkdown,
        AgentOptionsDto? options,
        CancellationToken cancellationToken);

    Task<GeneratedArticleImage> GenerateSingleArticleImageAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string title,
        string articleMarkdown,
        AgentOptionsDto? options,
        CancellationToken cancellationToken);

    IAsyncEnumerable<GeneratedArticleImage> GenerateSingleArticleImageStreamAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string title,
        string articleMarkdown,
        AgentOptionsDto? options,
        CancellationToken cancellationToken);

    Task<GeneratedArticleImage> GenerateFromPromptAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string title,
        string prompt,
        AgentOptionsDto? options,
        CancellationToken cancellationToken);

    IAsyncEnumerable<GeneratedArticleImage> GenerateFromPromptStreamAsync(
        AiProvider? provider,
        AiModel? imageModel,
        string title,
        string prompt,
        AgentOptionsDto? options,
        CancellationToken cancellationToken);
}

namespace VeraMedia.Api.Services;

public sealed record WebPageContent(string Url, string? Title, string Content);
public sealed record WebPageFetchResult(string Url, WebPageContent? Page, string? Error);

public interface IWebPageContentService
{
    IReadOnlyList<string> ExtractUrls(string text);
    Task<IReadOnlyList<WebPageFetchResult>> FetchAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken);
}

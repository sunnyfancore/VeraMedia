namespace VeraMedia.Api.Contracts;

public sealed record ArticleAssetDto(
    long Id,
    long ProjectId,
    string Title,
    string Excerpt,
    string Platform,
    int Version,
    string Status,
    long? ConversationId,
    long? MessageId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ArticleVersionDto(
    long Id,
    long ProjectId,
    string Title,
    string Excerpt,
    string Body,
    string Platform,
    int Version,
    DateTime CreatedAt);

public sealed record ImageAssetDto(
    long Id,
    long ProjectId,
    string ProjectTitle,
    string Prompt,
    string? ImageUrl,
    string Status,
    DateTime CreatedAt);

public sealed record SaveArticleAssetRequest(long ConversationId, long MessageId, string? Title, string? Body);

public sealed record BatchDeleteArticlesRequest(IReadOnlyList<long> ProjectIds);

public sealed record BatchDeleteArticlesResponse(int DeletedCount);

public sealed record BatchDeleteImagesRequest(IReadOnlyList<long> ImageIds);

public sealed record BatchDeleteImagesResponse(int DeletedCount);

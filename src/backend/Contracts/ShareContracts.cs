namespace VeraMedia.Api.Contracts;

public sealed record CreateShareRequest(long? ConversationId, long? MessageId, string Title, string Body, string ExpiresIn);
public sealed record CreateShareResponse(long ArticleId, string ShareUrl, string Token, DateTime? ExpiresAt);

using VeraMedia.Api.Contracts;

namespace VeraMedia.Api.Services;

public interface IConversationService
{
    Task<IReadOnlyList<ConversationSummary>> ListAsync(long userId, CancellationToken cancellationToken, int page = 1, int pageSize = 80);
    Task<IReadOnlyList<MessageDto>> MessagesAsync(long userId, long conversationId, CancellationToken cancellationToken);
    Task<long> EnsureConversationAsync(long userId, long? conversationId, string firstMessage, CancellationToken cancellationToken);
    Task AddAssistantMessageAsync(long userId, long conversationId, string content, CancellationToken cancellationToken, string? messageType = null);
    Task<IReadOnlyList<ChatTurn>> BuildTurnsAsync(long userId, long conversationId, SendMessageRequest request, CancellationToken cancellationToken);
}

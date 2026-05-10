using VeraMedia.Api.Models;
using VeraMedia.Api.Contracts;

namespace VeraMedia.Api.Services;

public sealed record ChatTurn(string Role, string Content, IReadOnlyList<AttachmentDto>? Attachments = null);

public interface IAiChatClient
{
    IAsyncEnumerable<string> StreamReplyAsync(
        AiProvider? provider,
        AiModel? model,
        IReadOnlyList<ChatTurn> turns,
        AgentOptionsDto? options,
        CancellationToken cancellationToken);
}

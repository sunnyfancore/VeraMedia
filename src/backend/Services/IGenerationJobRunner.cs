using VeraMedia.Api.Contracts;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public interface IGenerationJobRunner
{
    Task<GenerationJobDto> CreateAsync(long userId, SendMessageRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<GenerationJobDto>> ListRunningAsync(long userId, long? conversationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GenerationJobSummaryDto>> ListAsync(long userId, long? conversationId, string? status, int limit, CancellationToken cancellationToken);
    Task<GenerationJobDto?> GetAsync(long userId, long jobId, CancellationToken cancellationToken);
    Task<GenerationJobDto?> CancelAsync(long userId, long jobId, CancellationToken cancellationToken);
    Task<GenerationJobDto?> RetryAsync(long userId, long jobId, CancellationToken cancellationToken);
    Task RunAsync(long jobId, CancellationToken cancellationToken);
}

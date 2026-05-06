namespace VeraMedia.Api.Services;

public interface IGenerationJobQueue
{
    ValueTask EnqueueAsync(long jobId, CancellationToken cancellationToken = default);
    ValueTask<long> DequeueAsync(CancellationToken cancellationToken);
}

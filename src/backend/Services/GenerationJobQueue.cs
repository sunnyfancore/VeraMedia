using System.Threading.Channels;

namespace VeraMedia.Api.Services;

public sealed class GenerationJobQueue : IGenerationJobQueue
{
    private readonly Channel<long> channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(long jobId, CancellationToken cancellationToken = default) =>
        channel.Writer.WriteAsync(jobId, cancellationToken);

    public ValueTask<long> DequeueAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAsync(cancellationToken);
}

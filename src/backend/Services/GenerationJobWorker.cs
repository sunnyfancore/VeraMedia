using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public sealed class GenerationJobWorker(
    IGenerationJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<GenerationJobWorker> logger) : BackgroundService
{
    private const int MaxConcurrentJobs = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedJobsAsync(stoppingToken);
        using var concurrency = new SemaphoreSlim(MaxConcurrentJobs);

        while (!stoppingToken.IsCancellationRequested)
        {
            long jobId;
            try
            {
                jobId = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await concurrency.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var runner = scope.ServiceProvider.GetRequiredService<IGenerationJobRunner>();
                    await runner.RunAsync(jobId, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Generation job {JobId} crashed.", jobId);
                }
                finally
                {
                    concurrency.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task RequeueUnfinishedJobsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobs = await db.GenerationJobs
                .Where(x => !new[] { GenerationJobStatuses.Completed, GenerationJobStatuses.Failed, GenerationJobStatuses.Canceled }.Contains(x.Status))
                .OrderBy(x => x.Id)
                .ToListAsync(stoppingToken);

            foreach (var job in jobs)
            {
                if (job.Status == GenerationJobStatuses.Running)
                {
                    job.Status = GenerationJobStatuses.Pending;
                    job.UpdatedAt = DateTime.UtcNow;
                    job.Version++;
                }

                await queue.EnqueueAsync(job.Id, stoppingToken);
            }

            if (jobs.Count > 0)
            {
                await db.SaveChangesAsync(stoppingToken);
                logger.LogInformation("Requeued {Count} unfinished generation jobs.", jobs.Count);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to requeue unfinished generation jobs.");
        }
    }
}

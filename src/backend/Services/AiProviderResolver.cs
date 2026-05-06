using Microsoft.EntityFrameworkCore;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public interface IAiProviderResolver
{
    Task<AiProvider?> GetActiveProviderAsync(long userId, CancellationToken cancellationToken);
    AiModel? GetEnabledModel(AiProvider? provider, string modelType);
}

public sealed class AiProviderResolver(AppDbContext db) : IAiProviderResolver
{
    public Task<AiProvider?> GetActiveProviderAsync(long userId, CancellationToken cancellationToken)
    {
        return db.AiProviders
            .Where(x => x.UserId == userId && x.Enabled)
            .Include(x => x.Models)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public AiModel? GetEnabledModel(AiProvider? provider, string modelType)
    {
        return provider?.Models.FirstOrDefault(x => x.Enabled && x.ModelType == modelType);
    }
}

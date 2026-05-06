using VeraMedia.Api.Contracts;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public static class AiProviderResponseMapper
{
    public static ProviderResponse ToResponse(AiProvider provider)
    {
        return new ProviderResponse(
            provider.Id,
            provider.Name,
            provider.BaseUrl,
            provider.Models.FirstOrDefault(x => x.ModelType == AiModelTypes.Chat)?.Name ?? "",
            provider.Models.FirstOrDefault(x => x.ModelType == AiModelTypes.Image)?.Name ?? "",
            provider.Enabled,
            !string.IsNullOrWhiteSpace(provider.ApiKey),
            MaskApiKey(provider.ApiKey));
    }

    public static void UpsertModel(AiProvider provider, string modelType, string modelName)
    {
        var normalized = modelName.Trim();
        var model = provider.Models.FirstOrDefault(x => x.ModelType == modelType);
        if (model is null)
        {
            provider.Models.Add(new AiModel
            {
                ModelType = modelType,
                Name = normalized,
                Enabled = !string.IsNullOrWhiteSpace(normalized)
            });
            return;
        }

        model.Name = normalized;
        model.Enabled = !string.IsNullOrWhiteSpace(normalized);
    }

    public static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "";
        }

        return apiKey.Length <= 8 ? "已配置" : $"{apiKey[..4]}...{apiKey[^4..]}";
    }
}

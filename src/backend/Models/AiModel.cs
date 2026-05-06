namespace VeraMedia.Api.Models;

using VeraMedia.Api.Services;

public sealed class AiModel
{
    public long Id { get; set; }
    public long ProviderId { get; set; }
    public AiProvider? Provider { get; set; }
    public string Name { get; set; } = "";
    public string ModelType { get; set; } = AiModelTypes.Chat;
    public string CapabilitiesJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
}

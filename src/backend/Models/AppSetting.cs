namespace VeraMedia.Api.Models;

public sealed class AppSetting
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

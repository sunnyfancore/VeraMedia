namespace VeraMedia.Api.Models;

public sealed class Article
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public ContentProject? Project { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Platform { get; set; } = "wechat";
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ArticleShare> Shares { get; set; } = [];
}

namespace VeraMedia.Api.Models;

public sealed class User
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Conversation> Conversations { get; set; } = [];
    public List<ContentProject> ContentProjects { get; set; } = [];
    public List<UsageLog> UsageLogs { get; set; } = [];
}

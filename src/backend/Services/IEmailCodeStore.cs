namespace VeraMedia.Api.Services;

public interface IEmailCodeStore
{
    void Save(string email, string code, TimeSpan ttl);
    bool Validate(string email, string code);
}

public sealed class InMemoryEmailCodeStore : IEmailCodeStore
{
    private readonly Dictionary<string, (string Code, DateTime ExpiresAt)> codes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    public void Save(string email, string code, TimeSpan ttl)
    {
        lock (gate)
        {
            codes[email.Trim().ToLowerInvariant()] = (code, DateTime.UtcNow.Add(ttl));
        }
    }

    public bool Validate(string email, string code)
    {
        lock (gate)
        {
            var key = email.Trim().ToLowerInvariant();
            if (!codes.TryGetValue(key, out var saved) || saved.ExpiresAt < DateTime.UtcNow || saved.Code != code.Trim())
            {
                return false;
            }

            codes.Remove(key);
            return true;
        }
    }
}

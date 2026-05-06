namespace VeraMedia.Api.Services;

public sealed class AuthOptions
{
    public bool AllowRegistration { get; set; } = true;
    public bool RequireEmailCode { get; set; } = false;
}

public sealed class EmailCodeOptions
{
    public bool Enabled { get; set; } = false;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "内容运营助手";
    public int CodeMinutes { get; set; } = 10;
    public bool ExposeCodeInDevelopment { get; set; } = true;
}

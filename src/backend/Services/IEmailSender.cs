using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace VeraMedia.Api.Services;

public interface IEmailSender
{
    Task SendCodeAsync(string email, string code, CancellationToken cancellationToken);
}

public sealed class SmtpEmailSender(IAppSettingsService appSettingsService) : IEmailSender
{
    public async Task SendCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        var options = await appSettingsService.GetAuthEmailSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(options.SmtpHost) ||
            string.IsNullOrWhiteSpace(options.FromEmail) ||
            string.IsNullOrWhiteSpace(options.UserName) ||
            string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException("邮箱服务未配置完整，请检查 SMTP Host、账号、授权码/密码和发件邮箱。");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName, options.FromEmail));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = "邮箱验证码";
        message.Body = new TextPart("plain")
        {
            Text = $"你的验证码是：{code}\n\n验证码 {options.CodeMinutes} 分钟内有效。如非本人操作，请忽略这封邮件。"
        };

        using var client = new SmtpClient { Timeout = 30000 };
        var socketOptions = options.EnableSsl
            ? options.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable
            : SecureSocketOptions.Auto;

        try
        {
            await client.ConnectAsync(options.SmtpHost, options.SmtpPort, socketOptions, cancellationToken);
            await client.AuthenticateAsync(options.UserName, options.Password, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException
                                   or IOException
                                   or AuthenticationException
                                   or SmtpCommandException
                                   or SmtpProtocolException)
        {
            throw new InvalidOperationException($"验证码邮件发送失败：{ex.Message}", ex);
        }
    }
}

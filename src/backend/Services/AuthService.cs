using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using VeraMedia.Api.Contracts;
using VeraMedia.Api.Data;
using VeraMedia.Api.Models;

namespace VeraMedia.Api.Services;

public sealed class AuthService(
    AppDbContext db,
    IOptions<JwtOptions> options,
    IEmailCodeStore emailCodeStore,
    IEmailSender emailSender,
    IAppSettingsService appSettingsService) : IAuthService
{
    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var settings = await appSettingsService.GetAuthEmailSettingsAsync(cancellationToken);
        if (!settings.AllowRegistration)
        {
            throw new InvalidOperationException("当前站点已关闭公开注册，请联系管理员创建账号。");
        }

        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Email == email, cancellationToken))
        {
            throw new InvalidOperationException("该邮箱已经注册。");
        }

        if (settings.RequireEmailCode)
        {
            ValidateEmailCode(settings, email, request.EmailCode);
        }

        var user = new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email.Split('@')[0] : request.DisplayName.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsEnabled = true
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return BuildResponse(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email, cancellationToken)
            ?? throw new InvalidOperationException("邮箱或密码不正确。");

        if (!user.IsEnabled)
        {
            throw new InvalidOperationException("账号已被禁用，请联系管理员。");
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            throw new InvalidOperationException("邮箱或密码不正确。");
        }

        return BuildResponse(user);
    }

    public async Task<SendEmailCodeResponse> SendEmailCodeAsync(SendEmailCodeRequest request, CancellationToken cancellationToken)
    {
        var settings = await appSettingsService.GetAuthEmailSettingsAsync(cancellationToken);
        if (!settings.EmailCodeEnabled)
        {
            return new SendEmailCodeResponse(false, "当前未开启邮件验证码。");
        }

        var code = Random.Shared.Next(100000, 999999).ToString();
        var minutes = Math.Max(1, settings.CodeMinutes);
        await emailSender.SendCodeAsync(request.Email, code, cancellationToken);
        emailCodeStore.Save(request.Email, code, TimeSpan.FromMinutes(minutes));

        return new SendEmailCodeResponse(true, "验证码已发送到邮箱。");
    }

    public async Task ChangePasswordAsync(long userId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("用户不存在。");
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
        {
            throw new InvalidOperationException("当前密码不正确。");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var settings = await appSettingsService.GetAuthEmailSettingsAsync(cancellationToken);
        if (!settings.EmailCodeEnabled)
        {
            throw new InvalidOperationException("当前未开启邮箱验证码服务。");
        }

        var email = request.Email.Trim().ToLowerInvariant();
        ValidateEmailCode(settings, email, request.EmailCode);
        var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email, cancellationToken)
            ?? throw new InvalidOperationException("用户不存在。");
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await db.SaveChangesAsync(cancellationToken);
    }

    private void ValidateEmailCode(AuthEmailSettings settings, string email, string code)
    {
        if (!settings.EmailCodeEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(code) || !emailCodeStore.Validate(email, code))
        {
            throw new InvalidOperationException("邮箱验证码不正确或已过期。");
        }
    }

    private AuthResponse BuildResponse(User user, DateTime? refreshableUntil = null)
    {
        var jwt = options.Value;
        var now = DateTime.UtcNow;
        var sessionRefreshableUntil = refreshableUntil ?? now.AddMinutes(jwt.RefreshWindowMinutes);
        if (sessionRefreshableUntil <= now)
        {
            throw new InvalidOperationException("登录已过期，请重新登录。");
        }

        var refreshUntilUnix = new DateTimeOffset(sessionRefreshableUntil).ToUnixTimeSeconds().ToString();
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("display_name", user.DisplayName),
            new Claim("is_admin", user.IsAdmin ? "true" : "false"),
            new Claim("refresh_until", refreshUntilUnix)
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey));
        var expiresAt = new[] { now.AddMinutes(jwt.AccessTokenMinutes), sessionRefreshableUntil }.Min();
        var token = new JwtSecurityToken(
            jwt.Issuer,
            jwt.Audience,
            claims,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new AuthResponse(
            new JwtSecurityTokenHandler().WriteToken(token),
            new UserProfile(user.Id, user.Email, user.DisplayName, user.IsAdmin),
            expiresAt,
            sessionRefreshableUntil);
    }

    public async Task<AuthResponse> RefreshAsync(long userId, DateTime refreshableUntil, CancellationToken cancellationToken)
    {
        if (refreshableUntil <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("登录已过期，请重新登录。");
        }

        var user = await db.Users.FindAsync([userId], cancellationToken)
            ?? throw new InvalidOperationException("用户不存在。");
        if (!user.IsEnabled)
            throw new InvalidOperationException("账号已被禁用。");
        return BuildResponse(user, refreshableUntil);
    }
}

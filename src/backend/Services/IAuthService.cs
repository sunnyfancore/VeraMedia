using VeraMedia.Api.Contracts;

namespace VeraMedia.Api.Services;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<SendEmailCodeResponse> SendEmailCodeAsync(SendEmailCodeRequest request, CancellationToken cancellationToken);
    Task ChangePasswordAsync(long userId, ChangePasswordRequest request, CancellationToken cancellationToken);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);
    Task<AuthResponse> RefreshAsync(long userId, DateTime refreshableUntil, CancellationToken cancellationToken);
}

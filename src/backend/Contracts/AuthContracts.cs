namespace VeraMedia.Api.Contracts;

public sealed record RegisterRequest(string Email, string Password, string DisplayName, string EmailCode);
public sealed record LoginRequest(string Email, string Password);
public sealed record AuthResponse(string AccessToken, UserProfile User, DateTime ExpiresAt, DateTime RefreshableUntil);
public sealed record UserProfile(long Id, string Email, string DisplayName, bool IsAdmin);
public sealed record SendEmailCodeRequest(string Email);
public sealed record SendEmailCodeResponse(bool Sent, string Message);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ResetPasswordRequest(string Email, string EmailCode, string NewPassword);

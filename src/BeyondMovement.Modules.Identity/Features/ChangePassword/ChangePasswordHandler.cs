using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeyondMovement.Modules.Identity.Features.ChangePassword;

public sealed class ChangePasswordHandler(
    IIdentityDbContext db,
    IPasswordHasher<User> passwordHasher,
    IAuditLogger audit,
    IClock clock,
    ILogger<ChangePasswordHandler> logger)
{
    public async Task<Result> HandleAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || user.Status == UserStatus.Deleted)
            return Result.Failure(IdentityErrors.InvalidCredentials);

        // A Google-only account has nothing to verify against. Forgot Password is the
        // documented route for setting a first local password.
        if (user.PasswordHash is null)
            return Result.Failure(IdentityErrors.PasswordNotSet);

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword);

        if (verification == PasswordVerificationResult.Failed)
        {
            logger.LogInformation("Change-password rejected for user {UserId}: current password wrong", userId);
            return Result.Failure(IdentityErrors.InvalidCredentials);
        }

        user.SetPasswordHash(passwordHasher.HashPassword(user, request.NewPassword), now);

        // Same reasoning as a reset: whoever else holds a session should lose it.
        var activeTokens = await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAtUtc == null)
            .ToListAsync(ct);

        foreach (var token in activeTokens)
            token.Revoke(now);

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("PasswordChanged", user.Id,
            $"Password changed while signed in; {activeTokens.Count} refresh token(s) revoked.", ct);

        logger.LogInformation("Password changed for user {UserId}", user.Id);

        return Result.Success();
    }
}

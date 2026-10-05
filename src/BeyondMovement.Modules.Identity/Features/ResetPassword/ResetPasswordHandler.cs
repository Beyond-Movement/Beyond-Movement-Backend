using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeyondMovement.Modules.Identity.Features.ResetPassword;

public sealed class ResetPasswordHandler(
    IIdentityDbContext db,
    IPasswordHasher<User> passwordHasher,
    ITokenService tokens,
    IAuditLogger audit,
    IClock clock,
    ILogger<ResetPasswordHandler> logger)
{
    public async Task<Result> HandleAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var hash = tokens.Hash(request.Token);

        // Untracked: only the owner, to know which user to lock. The token is read again under
        // the lock, so a refresh in progress commits first and its replacement is revoked below.
        var userId = await db.PasswordResetTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.UserId)
            .FirstOrDefaultAsync(ct);

        if (userId is null)
            return Result.Failure(IdentityErrors.InvalidResetToken);

        await using var transaction = await db.BeginUserSessionLockAsync(userId.Value, ct);

        var now = clock.UtcNow;
        var resetToken = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (resetToken is null || !resetToken.IsUsable(now))
            return Result.Failure(IdentityErrors.InvalidResetToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == resetToken.UserId, ct);
        if (user is null || user.Status == UserStatus.Deleted)
            return Result.Failure(IdentityErrors.InvalidResetToken);

        user.SetPasswordHash(passwordHasher.HashPassword(user, request.NewPassword), now);
        resetToken.MarkUsed(now);

        // Whoever prompted the reset may already hold a stolen session — end all of them.
        var activeTokens = await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAtUtc == null)
            .ToListAsync(ct);

        foreach (var token in activeTokens)
            token.Revoke(now);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await audit.WriteAsync("PasswordReset", user.Id,
            $"Password reset completed; {activeTokens.Count} refresh token(s) revoked.", ct);

        logger.LogInformation("Password reset completed for user {UserId}", user.Id);

        return Result.Success();
    }
}

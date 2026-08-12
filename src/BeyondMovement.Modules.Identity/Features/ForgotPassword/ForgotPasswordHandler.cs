using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeyondMovement.Modules.Identity.Features.ForgotPassword;

public sealed class ForgotPasswordHandler(
    IIdentityDbContext db,
    ITokenService tokens,
    IEmailSender email,
    IClock clock,
    ILogger<ForgotPasswordHandler> logger)
{
    /// <summary>
    /// Always succeeds, whether or not the email exists. Telling the caller "no such account"
    /// would turn this endpoint into an account-enumeration oracle.
    /// </summary>
    public async Task<Result> HandleAsync(
        ForgotPasswordRequest request, string resetUrlTemplate, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var address = request.Email.ToLowerInvariant();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == address, ct);

        if (user is null || user.Status == UserStatus.Deleted)
        {
            logger.LogInformation("Password reset requested for an address with no active account");
            return Result.Success();
        }

        var (raw, hash) = tokens.CreateRefreshToken();   // same 32 random bytes, stored hashed
        db.PasswordResetTokens.Add(PasswordResetToken.Issue(user.Id, hash, now));
        await db.SaveChangesAsync(ct);

        var link = resetUrlTemplate.Replace("{token}", Uri.EscapeDataString(raw), StringComparison.Ordinal);

        await email.SendAsync(
            user.Email,
            "Reset your Beyond Movement password",
            $"Use this link within {PasswordResetToken.LifetimeHours} hour(s) to set a new password: {link}",
            ct);

        // Note the user id, never the address or the token itself (CLAUDE.md section 7).
        logger.LogInformation("Password reset token issued for user {UserId}", user.Id);

        return Result.Success();
    }
}

using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Modules.Identity.Features.Login;

public sealed class LoginHandler(
    IIdentityDbContext db,
    IPasswordHasher<User> passwordHasher,
    ITokenService tokens,
    IClock clock,
    IOptions<JwtOptions> jwtOptions,
    ILogger<LoginHandler> logger)
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<Result<AuthResponse>> HandleAsync(LoginRequest request, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var email = request.Email.ToLowerInvariant();

        // Tracked read: a failed attempt mutates the user.
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // Unknown email and wrong password return the identical failure — never reveal which.
        if (user is null)
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidCredentials);

        if (user.IsLockedOut(now))
            return Result<AuthResponse>.Failure(IdentityErrors.LockedFor(user.LockedOutUntilUtc!.Value - now));

        if (user.Status == UserStatus.Deleted || user.PasswordHash is null)
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidCredentials);

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

        if (verification == PasswordVerificationResult.Failed)
        {
            user.RecordFailedLogin(now);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Failed login attempt {Attempt} for user {UserId}",
                user.FailedLoginAttempts, user.Id);
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidCredentials);
        }

        // A paused account has valid credentials but no access (BR-10).
        if (user.Status == UserStatus.Paused)
            return Result<AuthResponse>.Failure(IdentityErrors.AccountPaused);

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.SetPasswordHash(passwordHasher.HashPassword(user, request.Password), now);

        user.RecordSuccessfulLogin(now);

        var (rawRefresh, refreshHash) = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(RefreshToken.Issue(
            user.Id, refreshHash, familyId: Guid.NewGuid(), request.DeviceId, now, _jwt.RefreshTokenDays));

        await db.SaveChangesAsync(ct);

        logger.LogInformation("User {UserId} logged in", user.Id);

        return Result<AuthResponse>.Success(AuthResponseFactory.Create(user, tokens, rawRefresh, _jwt));
    }
}

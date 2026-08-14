using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Modules.Identity.Features.GoogleSignIn;

/// <summary>
/// Architecture section 7.3. Three branches, and the last one is the whole point:
/// Google sign-in is an authentication method, not a registration path (BR-01).
/// </summary>
public sealed class GoogleSignInHandler(
    IIdentityDbContext db,
    IGoogleTokenValidator googleTokens,
    ITokenService tokens,
    IClock clock,
    IOptions<JwtOptions> jwtOptions,
    ILogger<GoogleSignInHandler> logger)
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<Result<AuthResponse>> HandleAsync(GoogleSignInRequest request, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        var identity = await googleTokens.ValidateAsync(request.IdToken, ct);

        if (identity is null)
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidGoogleToken);

        // An unverified Google email proves nothing about who controls that inbox, and the
        // whole account-matching branch below rests on the email being trustworthy.
        if (!identity.EmailVerified)
        {
            logger.LogWarning("Google sign-in rejected: the Google account's email is not verified");
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidGoogleToken);
        }

        var email = identity.Email.ToLowerInvariant();

        // Branch 1: we already know this Google account.
        var user = await db.Users.FirstOrDefaultAsync(u => u.GoogleSubjectId == identity.Subject, ct);

        // Branch 2: a password account exists for the same verified email — link them.
        if (user is null)
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

            if (user is not null)
            {
                user.LinkGoogleAccount(identity.Subject, now);
                logger.LogInformation("Linked a Google account to existing user {UserId}", user.Id);
            }
        }

        // Branch 3: no account. Turn them away — never create one here (BR-01).
        if (user is null)
        {
            logger.LogInformation("Google sign-in refused: no invitation exists for this address");
            return Result<AuthResponse>.Failure(IdentityErrors.InvitationRequired);
        }

        if (user.Status == UserStatus.Deleted)
            return Result<AuthResponse>.Failure(IdentityErrors.InvitationRequired);

        if (user.Status == UserStatus.Paused)
            return Result<AuthResponse>.Failure(IdentityErrors.AccountPaused);

        user.RecordSuccessfulLogin(now);

        var (rawRefresh, refreshHash) = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(RefreshToken.Issue(
            user.Id, refreshHash, familyId: Guid.NewGuid(), request.DeviceId, now, _jwt.RefreshTokenDays));

        await db.SaveChangesAsync(ct);

        logger.LogInformation("User {UserId} signed in with Google", user.Id);

        return Result<AuthResponse>.Success(AuthResponseFactory.Create(user, tokens, rawRefresh, _jwt));
    }
}

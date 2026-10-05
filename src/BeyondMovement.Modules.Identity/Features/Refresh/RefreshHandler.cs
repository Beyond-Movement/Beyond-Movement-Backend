using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Namespace is "Refresh", not "RefreshToken": a namespace segment matching the entity
// name would shadow the RefreshToken type inside this module.
namespace BeyondMovement.Modules.Identity.Features.Refresh;

/// <summary>
/// Rotation is atomic: a refresh token is spent successfully exactly once, however many
/// requests present it and however many API instances serve them.
/// <list type="bullet">
/// <item>The whole rotation - spend the old token, issue the new one - is one transaction, so
/// there is never a spent token without its replacement.</item>
/// <item>The user's row is locked first - <see cref="UserSessionLock"/>, the lock every operation
/// that ends sessions takes in the same order - so the token's state is read after any rotation,
/// logout, password change, reset or pause in progress has committed, and a replacement can never
/// be inserted behind a revocation that has already read the user's tokens.</item>
/// <item>The token is spent with a conditional update (<c>UsedAtUtc IS NULL AND RevokedAtUtc IS
/// NULL</c>) that must touch exactly one row - the database's own guarantee that it is spent once,
/// independent of the lock.</item>
/// </list>
/// </summary>
public sealed class RefreshHandler(
    IIdentityDbContext db,
    ITokenService tokens,
    IClock clock,
    IOptions<JwtOptions> jwtOptions,
    ILogger<RefreshHandler> logger)
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<Result<AuthResponse>> HandleAsync(RefreshRequest request, CancellationToken ct = default)
    {
        // No token at all - an empty body, or a browser with no cookie - is simply not a valid one.
        if (string.IsNullOrEmpty(request.RefreshToken))
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidRefreshToken);

        var hash = tokens.Hash(request.RefreshToken);

        // Untracked: only the owner is needed here, to know which row to lock. The state that
        // decides the outcome is read again under the lock.
        var userId = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.UserId)
            .FirstOrDefaultAsync(ct);

        if (userId is null)
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidRefreshToken);

        await using var transaction = await db.BeginUserSessionLockAsync(userId.Value, ct);

        // Read after the lock, so "now" is never earlier than a rotation committed while waiting.
        var now = clock.UtcNow;
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null)
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidRefreshToken);

        if (stored.UsedAtUtc is not null)
        {
            // Spent moments ago by another request with this same token: the client's own
            // duplicate. Issue nothing - a second replacement would fork the family - and revoke
            // nothing, because this is not a replay.
            if (stored.WasJustRotated(now))
            {
                logger.LogInformation(
                    "Superseded refresh for user {UserId} in family {FamilyId}: the token was rotated {Seconds:0.0}s ago",
                    stored.UserId, stored.FamilyId, (now - stored.UsedAtUtc.Value).TotalSeconds);

                return Result<AuthResponse>.Failure(IdentityErrors.RefreshSuperseded);
            }

            // Reuse detection. A token that was already spent is now in two places at once,
            // so the family is treated as compromised and every token in it dies.
            var family = await db.RefreshTokens
                .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAtUtc == null)
                .ToListAsync(ct);

            foreach (var token in family)
                token.Revoke(now);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogWarning(
                "Refresh token reuse detected for user {UserId}; revoked {Count} tokens in family {FamilyId}",
                stored.UserId, family.Count, stored.FamilyId);

            return Result<AuthResponse>.Failure(IdentityErrors.InvalidRefreshToken);
        }

        // Revoked or expired.
        if (!stored.IsActive(now))
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidRefreshToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null || user.Status == UserStatus.Deleted)
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidRefreshToken);

        if (user.Status == UserStatus.Paused)
            return Result<AuthResponse>.Failure(IdentityErrors.AccountPaused);

        var spent = await db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.UsedAtUtc == null && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAtUtc, now), ct);

        // Under the lock nothing else can have spent or revoked it since the read above; this is
        // the database confirming it rather than a path expected to run.
        if (spent != 1)
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidRefreshToken);

        // Rotation: a new pair, same family, so reuse of the old one is still detectable.
        var (rawRefresh, refreshHash) = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(RefreshToken.Issue(
            user.Id, refreshHash, stored.FamilyId, request.DeviceId ?? stored.DeviceId, now, _jwt.RefreshTokenDays));

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Result<AuthResponse>.Success(AuthResponseFactory.Create(user, tokens, rawRefresh, _jwt));
    }
}

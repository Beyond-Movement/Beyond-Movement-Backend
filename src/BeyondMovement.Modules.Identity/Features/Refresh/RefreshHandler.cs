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
        var now = clock.UtcNow;
        var hash = tokens.Hash(request.RefreshToken);

        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null)
            return Result<AuthResponse>.Failure(IdentityErrors.InvalidRefreshToken);

        // Reuse detection. A token that was already spent is now in two places at once,
        // so the family is treated as compromised and every token in it dies.
        if (stored.UsedAtUtc is not null)
        {
            var family = await db.RefreshTokens
                .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAtUtc == null)
                .ToListAsync(ct);

            foreach (var token in family)
                token.Revoke(now);

            await db.SaveChangesAsync(ct);

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

        stored.MarkUsed(now);

        // Rotation: a new pair, same family, so reuse of the old one is still detectable.
        var (rawRefresh, refreshHash) = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(RefreshToken.Issue(
            user.Id, refreshHash, stored.FamilyId, request.DeviceId ?? stored.DeviceId, now, _jwt.RefreshTokenDays));

        await db.SaveChangesAsync(ct);

        return Result<AuthResponse>.Success(new AuthResponse(
            tokens.CreateAccessToken(user),
            rawRefresh,
            _jwt.AccessTokenMinutes * 60,
            new UserSummary(user.Id, user.Role.ToString(), user.FullName, user.Email)));
    }
}

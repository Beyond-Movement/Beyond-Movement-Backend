using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeyondMovement.Modules.Identity.Features.AccountStatus;

/// <summary>
/// Pausing and reactivating an athlete account (BR-10).
/// <para>
/// Lives in Identity rather than Athletes because it changes <c>Users.Status</c> and revokes
/// refresh tokens — both squarely identity concerns. The athlete's profile is untouched.
/// </para>
/// </summary>
public sealed class SetAccountStatusHandler(
    IIdentityDbContext db,
    IClock clock,
    IAuditLogger audit,
    ILogger<SetAccountStatusHandler> logger)
{
    public async Task<Result<UserStatus>> PauseAsync(
        Guid coachId, Guid athleteUserId, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var user = await FindAthleteAsync(coachId, athleteUserId, ct);

        if (user is null)
            return Result<UserStatus>.Failure(IdentityErrors.AthleteNotFound);

        if (user.Status == UserStatus.Paused)
            return Result<UserStatus>.Success(user.Status);   // idempotent: already where the caller wants it

        user.Pause(now);

        // The access token stays valid for its remaining minutes, which the per-request status
        // check in the pipeline closes. The refresh tokens are what would otherwise let the
        // athlete keep renewing indefinitely, so they die here.
        var revoked = await RevokeRefreshTokensAsync(user.Id, now, ct);

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("AthletePaused", actorUserId,
            $"Athlete {user.Id} paused; {revoked} refresh token(s) revoked.", ct);

        logger.LogInformation("Athlete {UserId} paused; {Count} refresh tokens revoked", user.Id, revoked);

        return Result<UserStatus>.Success(user.Status);
    }

    public async Task<Result<UserStatus>> ReactivateAsync(
        Guid coachId, Guid athleteUserId, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var user = await FindAthleteAsync(coachId, athleteUserId, ct);

        if (user is null)
            return Result<UserStatus>.Failure(IdentityErrors.AthleteNotFound);

        if (user.Status == UserStatus.Active)
            return Result<UserStatus>.Success(user.Status);

        user.Reactivate(now);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("AthleteReactivated", actorUserId, $"Athlete {user.Id} reactivated.", ct);

        // Deliberately issues nothing: the athlete signs in again themselves.
        logger.LogInformation("Athlete {UserId} reactivated", user.Id);

        return Result<UserStatus>.Success(user.Status);
    }

    private async Task<int> RevokeRefreshTokensAsync(Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        var tokens = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ToListAsync(ct);

        foreach (var token in tokens)
            token.Revoke(nowUtc);

        return tokens.Count;
    }

    /// <summary>
    /// Scoped to the coach and to the Athlete role. Another coach's athlete, an unknown id, or
    /// the Admin's own record are all "not found" rather than "forbidden" — a 403 would confirm
    /// the row exists (CLAUDE.md section 6).
    /// </summary>
    private Task<User?> FindAthleteAsync(Guid coachId, Guid athleteUserId, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(
            u => u.Id == athleteUserId
                 && u.CoachId == coachId
                 && u.Role == UserRole.Athlete
                 && u.Status != UserStatus.Deleted,
            ct);
}

using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Identity.Features.Logout;

public sealed class LogoutHandler(IIdentityDbContext db, ITokenService tokens, IClock clock)
{
    /// <summary>
    /// Ends the session the presented refresh token belongs to: every token in its family. In the
    /// ordinary case that is the presented token alone - the rest of the family is already spent.
    /// It differs only when a refresh of the same token raced this logout and won: its replacement
    /// is in the same family, so it is revoked too rather than outliving the logout. Other
    /// sign-ins (other families) are untouched.
    /// <para>
    /// Succeeds even when the token is unknown - the caller learns nothing either way, and the
    /// end state is the same.
    /// </para>
    /// </summary>
    public async Task<Result> HandleAsync(LogoutRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            return Result.Success();

        var hash = tokens.Hash(request.RefreshToken);

        var owner = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.UserId, t.FamilyId })
            .FirstOrDefaultAsync(ct);

        if (owner is null)
            return Result.Success();

        // Before reading the family: a refresh in progress commits first, so its replacement is
        // among the tokens revoked below (UserSessionLock).
        await using var transaction = await db.BeginUserSessionLockAsync(owner.UserId, ct);

        var now = clock.UtcNow;
        var session = await db.RefreshTokens
            .Where(t => t.FamilyId == owner.FamilyId && t.RevokedAtUtc == null)
            .ToListAsync(ct);

        foreach (var token in session)
            token.Revoke(now);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Result.Success();
    }
}

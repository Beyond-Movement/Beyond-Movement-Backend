using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Identity.Features.Logout;

public sealed class LogoutHandler(IIdentityDbContext db, ITokenService tokens, IClock clock)
{
    /// <summary>
    /// Revokes the presented refresh token. Succeeds even when the token is unknown —
    /// the caller learns nothing either way, and the end state is the same.
    /// </summary>
    public async Task<Result> HandleAsync(LogoutRequest request, CancellationToken ct = default)
    {
        var hash = tokens.Hash(request.RefreshToken);

        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is not null)
        {
            stored.Revoke(clock.UtcNow);
            await db.SaveChangesAsync(ct);
        }

        return Result.Success();
    }
}

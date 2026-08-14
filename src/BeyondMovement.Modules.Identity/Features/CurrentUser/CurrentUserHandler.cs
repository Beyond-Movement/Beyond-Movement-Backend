using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Identity.Features.CurrentUser;

/// <summary>
/// Backs session restoration on app start. Reads live from the database rather than trusting
/// the token's claims, so a role or status changed since the token was issued is reflected.
/// </summary>
public sealed class CurrentUserHandler(IIdentityDbContext db)
{
    public async Task<Result<CurrentUserResponse>> HandleAsync(
        Guid userId, string minimumSupportedAppVersion, CancellationToken ct = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new CurrentUserResponse(
                u.Id,
                u.Role,
                u.Status,
                u.FullName,
                u.Email,
                u.CoachId,
                u.ProfileCompletedAtUtc != null,
                minimumSupportedAppVersion))
            .FirstOrDefaultAsync(ct);

        return user is null
            ? Result<CurrentUserResponse>.Failure(IdentityErrors.InvalidRefreshToken)
            : Result<CurrentUserResponse>.Success(user);
    }
}

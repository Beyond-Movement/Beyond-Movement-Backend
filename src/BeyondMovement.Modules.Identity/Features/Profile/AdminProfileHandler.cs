using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Identity.Features.Profile;

/// <summary>
/// Reads and writes the signed-in Admin's own profile. Always the caller's own record — the user
/// id comes from the token and there is no id in the route or the body, so there is nothing to
/// authorise beyond being signed in as an Admin.
/// </summary>
public sealed class AdminProfileHandler(IIdentityDbContext db, IClock clock)
{
    public async Task<Result<AdminProfileResponse>> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var profile = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new AdminProfileResponse(u.Id, u.FullName, u.Email, u.Phone))
            .FirstOrDefaultAsync(ct);

        return profile is null
            ? Result<AdminProfileResponse>.Failure(IdentityErrors.InvalidCredentials)
            : Result<AdminProfileResponse>.Success(profile);
    }

    /// <summary>
    /// A full replacement of both editable fields. The email on the record is never touched:
    /// it is not in the request, and nothing here can reach it.
    /// </summary>
    public async Task<Result<AdminProfileResponse>> UpdateAsync(
        Guid userId, UpdateAdminProfileRequest request, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || user.Status == UserStatus.Deleted)
            return Result<AdminProfileResponse>.Failure(IdentityErrors.InvalidCredentials);

        var now = clock.UtcNow;

        // The validator has already refused a blank name, so SetFullName's own guard is the
        // second line rather than the first.
        user.SetFullName(request.FullName, now);
        user.SetPhone(request.Phone, now);

        await db.SaveChangesAsync(ct);

        // Read back off the entity rather than echoed from the request: both setters trim, and
        // a blank phone becomes null, so what was stored is not always what was sent.
        return Result<AdminProfileResponse>.Success(
            new AdminProfileResponse(user.Id, user.FullName, user.Email, user.Phone));
    }
}

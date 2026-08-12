using BeyondMovement.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Identity.Persistence;

/// <summary>
/// The slice of the database this module is allowed to touch.
/// <para>
/// Modules must not reference Infrastructure (CLAUDE.md section 4), so handlers cannot use
/// <c>AppDbContext</c> directly. Infrastructure implements this interface on AppDbContext and
/// the Api registers the mapping. The module depends on the abstraction, not the database.
/// </para>
/// </summary>
public interface IIdentityDbContext
{
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

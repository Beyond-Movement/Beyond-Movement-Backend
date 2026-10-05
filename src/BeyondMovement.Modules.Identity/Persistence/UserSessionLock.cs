using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BeyondMovement.Modules.Identity.Persistence;

/// <summary>
/// The single lock that orders every change to a user's sessions.
/// <para>
/// <b>Lock order, everywhere:</b> first the user's row in <c>Users</c> (<c>FOR UPDATE</c>), then
/// that user's <c>RefreshTokens</c> and <c>PasswordResetTokens</c> rows, all in one transaction.
/// Refresh, logout, password change, password reset, pause and reactivate all take it before
/// reading anything that decides their outcome. Because every one of them takes the same lock
/// first, they queue instead of interleaving:
/// </para>
/// <list type="bullet">
/// <item>An operation that revokes tokens reads the user's tokens only after any refresh in
/// progress has committed, so a replacement inserted by that refresh is revoked too - it cannot
/// survive.</item>
/// <item>No two of them can hold token rows while waiting for the user row, which is how a refresh
/// and a pause used to deadlock (PostgreSQL 40P01, surfacing as a 500).</item>
/// </list>
/// <para>
/// One user per transaction: nothing here locks two users, so the order between users never
/// arises.
/// </para>
/// </summary>
internal static class UserSessionLock
{
    /// <summary>
    /// Begins a transaction and locks the user's row. Read the state that decides the outcome
    /// after this returns, never before. Taking the lock on a user that does not exist locks
    /// nothing and is harmless.
    /// </summary>
    public static async Task<IDbContextTransaction> BeginUserSessionLockAsync(
        this IIdentityDbContext db, Guid userId, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            await db.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM "Users" WHERE "Id" = {userId} FOR UPDATE""", ct);

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}

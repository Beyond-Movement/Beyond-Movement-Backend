using BeyondMovement.SharedKernel;

namespace BeyondMovement.Infrastructure.Auditing;

public sealed class AuditLogger(AppDbContext db, IClock clock) : IAuditLogger
{
    public async Task WriteAsync(string action, Guid? actorUserId, string? details, CancellationToken ct = default)
    {
        db.AuditLogs.Add(AuditLog.Record(action, actorUserId, details, clock.UtcNow));
        await db.SaveChangesAsync(ct);
    }
}

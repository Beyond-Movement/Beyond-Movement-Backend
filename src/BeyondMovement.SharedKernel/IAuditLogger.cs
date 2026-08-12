namespace BeyondMovement.SharedKernel;

/// <summary>
/// Writes entries to the AuditLogs table for anything with legal or financial weight
/// (CLAUDE.md section 7). Implemented in Infrastructure; modules depend only on this.
/// </summary>
public interface IAuditLogger
{
    Task WriteAsync(string action, Guid? actorUserId, string? details, CancellationToken ct = default);
}

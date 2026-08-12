using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BeyondMovement.Infrastructure.Auditing;

/// <summary>
/// Append-only record of anything with legal or financial weight (CLAUDE.md section 7).
/// In production the application role gets INSERT only — no UPDATE, no DELETE.
/// </summary>
public sealed class AuditLog
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Action { get; private set; } = null!;
    public Guid? ActorUserId { get; private set; }
    public string? Details { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    private AuditLog() { }

    public static AuditLog Record(string action, Guid? actorUserId, string? details, DateTime nowUtc) => new()
    {
        Action = action,
        ActorUserId = actorUserId,
        Details = details,
        OccurredAtUtc = nowUtc
    };
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.HasKey(x => x.Id);

        b.Property(x => x.Action).IsRequired().HasMaxLength(100);
        b.Property(x => x.Details).HasMaxLength(2000);

        b.HasIndex(x => x.OccurredAtUtc);
        b.HasIndex(x => x.ActorUserId);
    }
}

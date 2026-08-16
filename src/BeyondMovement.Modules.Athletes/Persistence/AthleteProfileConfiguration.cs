using BeyondMovement.Modules.Athletes.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BeyondMovement.Modules.Athletes.Persistence;

public sealed class AthleteProfileConfiguration : IEntityTypeConfiguration<AthleteProfile>
{
    public void Configure(EntityTypeBuilder<AthleteProfile> b)
    {
        b.ToTable("AthleteProfiles");
        b.HasKey(x => x.Id);

        // One profile per user (architecture section 6.3).
        b.HasIndex(x => x.UserId).IsUnique();

        b.Property(x => x.Sport).HasMaxLength(100);

        // Stored as its name, never its ordinal (CLAUDE.md section 7): the rows stay readable
        // and reordering the enum cannot silently turn every Female row into a Male one.
        b.Property(x => x.Gender).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.Notes).HasMaxLength(4000);

        // Serves the athlete list, which is always scoped to the coach and hides deleted rows.
        b.HasIndex(x => new { x.CoachId, x.DeletedAtUtc });
    }
}

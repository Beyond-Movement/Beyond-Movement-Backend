using BeyondMovement.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BeyondMovement.Modules.Identity.Persistence;

public sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> b)
    {
        b.ToTable("Invitations");
        b.HasKey(x => x.Id);

        b.Property(x => x.Email).IsRequired().HasMaxLength(256);
        b.Property(x => x.CodeHash).IsRequired().HasMaxLength(128);
        b.HasIndex(x => x.CodeHash).IsUnique();

        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // The athlete list and the invitations screen both filter by coach and status.
        b.HasIndex(x => new { x.CoachId, x.Status });

        // At most one live invitation per address, enforced by the database and not only by
        // the handler — two pending codes for one athlete would break "each invitation can be
        // used only for its intended athlete" in spirit and confuse the inbox.
        b.HasIndex(x => x.Email)
            .IsUnique()
            .HasFilter("\"Status\" = 'Pending'");
    }
}

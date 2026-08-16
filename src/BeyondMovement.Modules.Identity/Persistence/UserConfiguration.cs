using BeyondMovement.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BeyondMovement.Modules.Identity.Persistence;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users");
        b.HasKey(x => x.Id);

        b.Property(x => x.Email).IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.Email).IsUnique();

        b.Property(x => x.GoogleSubjectId).HasMaxLength(128);
        b.HasIndex(x => x.GoogleSubjectId).IsUnique().HasFilter("\"GoogleSubjectId\" IS NOT NULL");

        // Nullable on purpose: an athlete exists between registering and completing their
        // profile, and has no name during that window. The invariant "completed implies named"
        // is kept by User.MarkProfileCompleted, which a column constraint cannot express.
        b.Property(x => x.FullName).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(40);
        b.Property(x => x.TimeZone).IsRequired().HasMaxLength(64);

        // enums as strings — readable in the database, immune to reordering
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        b.Property(x => x.UiPreferences).HasColumnType("jsonb");
        b.Property(x => x.NotificationPreferences).HasColumnType("jsonb");

        b.HasIndex(x => new { x.CoachId, x.Role, x.Status });
    }
}

using BeyondMovement.Modules.Athletes.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BeyondMovement.Modules.Athletes.Persistence;

public sealed class SportConfiguration : IEntityTypeConfiguration<Sport>
{
    public void Configure(EntityTypeBuilder<Sport> b)
    {
        b.ToTable("Sports");
        b.HasKey(x => x.Id);

        // Ids are fixed in SportCatalogue, never generated.
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).IsRequired().HasMaxLength(100);

        // Names are unique ignoring case, so "tennis" cannot be added beside "Tennis". That index
        // is on lower("Name") and EF cannot express it, so the migration creates it in SQL: see
        // IX_Sports_Name_CaseInsensitive in AddSportsCatalogue.

        // The catalogue is seeded from the model, so a new sport arrives with a migration.
        b.HasData(SportCatalogue.All);
    }
}

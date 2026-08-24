using BeyondMovement.Modules.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BeyondMovement.Modules.Scheduling.Persistence;

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.ToTable("Sessions", t =>
        {
            t.HasCheckConstraint("CK_Sessions_TimeRange", "\"ScheduledEndUtc\" > \"ScheduledStartUtc\"");
            t.HasCheckConstraint("CK_Sessions_Duration", "\"DurationMinutes\" > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.CalendlyEventUri).HasMaxLength(500).IsRequired();
        b.Property(x => x.CalendlyInviteeUri).HasMaxLength(500).IsRequired();
        b.Property(x => x.CalendlyEventTypeUri).HasMaxLength(500).IsRequired();
        b.Property(x => x.DeliveryType).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.LocationOrPlatform).HasMaxLength(500);
        b.Property(x => x.MeetingUrl).HasMaxLength(1000);
        b.Property(x => x.CancelUrl).HasMaxLength(1000);
        b.Property(x => x.RescheduleUrl).HasMaxLength(1000);
        b.Property(x => x.CancellationReason).HasMaxLength(1000);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.CalendlyEventUri).IsUnique();
        b.HasIndex(x => x.CalendlyInviteeUri).IsUnique();
        b.HasIndex(x => new { x.CoachId, x.ScheduledStartUtc, x.Status });
        b.HasIndex(x => new { x.AthleteProfileId, x.ScheduledStartUtc });
    }
}

public sealed class CalendlyWebhookEventConfiguration : IEntityTypeConfiguration<CalendlyWebhookEvent>
{
    public void Configure(EntityTypeBuilder<CalendlyWebhookEvent> b)
    {
        b.ToTable("CalendlyWebhookEvents");
        b.HasKey(x => x.Id);
        b.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
        b.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        b.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.Status, x.ReceivedAtUtc });
    }
}

public sealed class BookingOperationConfiguration : IEntityTypeConfiguration<BookingOperation>
{
    public void Configure(EntityTypeBuilder<BookingOperation> b)
    {
        b.ToTable("SchedulingBookingOperations");
        b.HasKey(x => x.Id);
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.AthleteProfileId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => x.CreatedAtUtc);
    }
}

public sealed class SchedulingChangeConfiguration : IEntityTypeConfiguration<SchedulingChange>
{
    public void Configure(EntityTypeBuilder<SchedulingChange> b)
    {
        b.ToTable("SchedulingChanges"); b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.DedupKey).HasMaxLength(1000).IsRequired();
        b.HasIndex(x => x.DedupKey).IsUnique();
        b.HasIndex(x => new { x.PublishedAtUtc, x.OccurredAtUtc });
    }
}

public sealed class CalendlyUnmatchedBookingConfiguration : IEntityTypeConfiguration<CalendlyUnmatchedBooking>
{
    public void Configure(EntityTypeBuilder<CalendlyUnmatchedBooking> b)
    {
        b.ToTable("CalendlyUnmatchedBookings"); b.HasKey(x => x.Id);
        b.Property(x => x.CalendlyEventUri).HasMaxLength(500).IsRequired();
        b.Property(x => x.CalendlyInviteeUri).HasMaxLength(500).IsRequired();
        b.Property(x => x.CalendlyEventTypeUri).HasMaxLength(500).IsRequired();
        b.Property(x => x.InviteeEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.HasIndex(x => x.CalendlyInviteeUri).IsUnique();
        b.HasIndex(x => new { x.ResolvedAtUtc, x.DiscoveredAtUtc });
    }
}

public sealed class CalendlyReconciliationRunConfiguration : IEntityTypeConfiguration<CalendlyReconciliationRun>
{
    public void Configure(EntityTypeBuilder<CalendlyReconciliationRun> b)
    {
        b.ToTable("CalendlyReconciliationRuns"); b.HasKey(x => x.Id);
        b.Property(x => x.Error).HasMaxLength(1000);
        b.HasIndex(x => x.StartedAtUtc);
    }
}

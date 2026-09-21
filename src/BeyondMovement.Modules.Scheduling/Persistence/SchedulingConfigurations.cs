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
            t.HasCheckConstraint("CK_Sessions_Consumed",
                "\"ConsumedSessionCount\" IN (0, 1)");

            // BR-04 and BR-06 as a constraint rather than a convention: only a session that
            // actually happened may record having consumed one.
            t.HasCheckConstraint("CK_Sessions_ConsumedOnlyWhenResolved",
                "\"ConsumedSessionCount\" = 0 OR \"Status\" IN ('Attended', 'NoShow')");
            t.HasCheckConstraint("CK_Sessions_PackagePositionMatchesConsumption",
                "(\"ConsumedSessionCount\" = 0 AND \"ConsumedPackagePosition\" IS NULL) OR " +
                "(\"ConsumedSessionCount\" = 1 AND \"ConsumedPackagePosition\" > 0)");

            // BR-07. The Admin's deduction choice belongs to observations and only to them, so
            // the column is non-null exactly when the session is one. Stated here because the
            // generated schema cannot express "required for this delivery type"; the database
            // can, and it is the one place the rule cannot be forgotten.
            t.HasCheckConstraint("CK_Sessions_ObservationDeductsSession",
                "(\"DeliveryType\" = 'Observation') = (\"ObservationDeductsSession\" IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        // Nullable since Phase 6: an Observation is recorded by the Admin and has no Calendly
        // event behind it. The unique indexes below still hold - Postgres treats nulls as
        // distinct, so any number of observations can sit beside the booked sessions.
        b.Property(x => x.CalendlyEventUri).HasMaxLength(500);
        b.Property(x => x.CalendlyInviteeUri).HasMaxLength(500);
        b.Property(x => x.CalendlyEventTypeUri).HasMaxLength(500);
        b.Property(x => x.DeliveryType).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.LocationOrPlatform).HasMaxLength(500);
        b.Property(x => x.MeetingUrl).HasMaxLength(1000);
        b.Property(x => x.CancelUrl).HasMaxLength(1000);
        b.Property(x => x.RescheduleUrl).HasMaxLength(1000);
        b.Property(x => x.CancellationReason).HasMaxLength(1000);

        // 0 or 1, and never more than that even if application code goes wrong.
        b.Property(x => x.ConsumedSessionCount).IsRequired();
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
        // Nullable since Phase 6: an Observation is recorded by the Admin and has no Calendly
        // event behind it. The unique indexes below still hold - Postgres treats nulls as
        // distinct, so any number of observations can sit beside the booked sessions.
        b.Property(x => x.CalendlyEventUri).HasMaxLength(500);
        b.Property(x => x.CalendlyInviteeUri).HasMaxLength(500);
        b.Property(x => x.CalendlyEventTypeUri).HasMaxLength(500);
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

public sealed class ObservationRequestConfiguration : IEntityTypeConfiguration<ObservationRequest>
{
    public void Configure(EntityTypeBuilder<ObservationRequest> b)
    {
        b.ToTable("ObservationRequests", t =>
        {
            t.HasCheckConstraint("CK_ObservationRequests_Duration",
                $"\"RequestedDurationMinutes\" BETWEEN {ObservationRequest.MinDurationMinutes} " +
                $"AND {ObservationRequest.MaxDurationMinutes}");

            // Accepted is not a status that can be set on its own: it has to carry the session it
            // produced, or the schedule has a request that says the coach agreed and cannot say
            // to what. Every other status must carry none, so a declined request cannot quietly
            // keep a link to a session it never made. The same shape as
            // CK_PackagePurchases_PaidConsistency, for the same reason.
            t.HasCheckConstraint("CK_ObservationRequests_AcceptedHasSession",
                "(\"Status\" = 'Accepted' AND \"SessionId\" IS NOT NULL) OR " +
                "(\"Status\" <> 'Accepted' AND \"SessionId\" IS NULL)");

            // Pending means still waiting, and waiting has no moment it ended. The three
            // terminal states all have one.
            t.HasCheckConstraint("CK_ObservationRequests_ResolvedConsistency",
                "(\"Status\" = 'Pending' AND \"ResolvedAtUtc\" IS NULL AND \"ResolvedByUserId\" IS NULL) OR " +
                "(\"Status\" <> 'Pending' AND \"ResolvedAtUtc\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL)");
        });

        b.HasKey(x => x.Id);

        b.Property(x => x.Location).IsRequired().HasMaxLength(ObservationRequest.MaxLocationLength);
        b.Property(x => x.Details).HasMaxLength(ObservationRequest.MaxDetailsLength);
        b.Property(x => x.RequestedDurationMinutes).IsRequired();

        // Stored as a string, like every other enum in this database: readable during support,
        // and immune to the reordering mistake that renumbers an integer enum.
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Maps to Postgres' xmin rather than a column of its own, as Session does.
        b.Property(x => x.Version).IsRowVersion();

        // One request per session. The half of "a repeated accept produces exactly one session"
        // that survives a bug in the handler. Filtered, because every request that has not been
        // accepted has a null here and nulls would otherwise collide.
        b.HasIndex(x => x.SessionId).IsUnique()
            .HasFilter("\"SessionId\" IS NOT NULL")
            .HasDatabaseName("IX_ObservationRequests_OneRequestPerSession");

        // The Admin's review queue: this coach's requests, filtered by status, soonest first.
        b.HasIndex(x => new { x.CoachId, x.Status, x.RequestedStartUtc });

        // The athlete's own list, newest first.
        b.HasIndex(x => new { x.AthleteProfileId, x.CreatedAtUtc });

        // A computed property, not a column. Left alone, EF maps it and the schedule then has
        // two ends that can disagree - the trap CLAUDE.md section 7.4 records.
        b.Ignore(x => x.RequestedEndUtc);

        // The relationships to AthleteProfile and Session are declared in AppDbContext. This
        // file may name Session, but the athlete profile belongs to another module, so both are
        // wired in the one place that sees the whole graph rather than being split across two.
    }
}

public sealed class SessionNoteConfiguration : IEntityTypeConfiguration<SessionNote>
{
    public void Configure(EntityTypeBuilder<SessionNote> b)
    {
        b.ToTable("SessionNotes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).IsRequired().HasMaxLength(SessionNote.MaxTitleLength);
        b.Property(x => x.Content).IsRequired().HasMaxLength(SessionNote.MaxContentLength);

        // The two reads: one session's notes, and the athlete's history assembled from the
        // sessions that belong to them.
        b.HasIndex(x => new { x.SessionId, x.CreatedAtUtc });

        b.HasOne<Session>().WithMany()
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

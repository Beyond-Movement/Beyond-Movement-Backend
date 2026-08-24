using BeyondMovement.Modules.Scheduling.Domain;

namespace BeyondMovement.UnitTests.Scheduling;

public sealed class SessionTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_persists_UTC_duration_and_does_not_contain_package_deduction_state()
    {
        var data = Data(Now.AddDays(1), Now.AddDays(1).AddMinutes(60));
        var session = Session.Create(Guid.NewGuid(), Guid.NewGuid(), data, Now);

        Assert.Equal(SessionStatus.Scheduled, session.Status);
        Assert.Equal(60, session.DurationMinutes);
        Assert.Equal(DateTimeKind.Utc, session.ScheduledStartUtc.Kind);
        Assert.Null(session.PackageId);
    }

    [Fact]
    public void Synchronize_reschedule_keeps_local_identity_and_athlete()
    {
        var athlete = Guid.NewGuid();
        var session = Session.Create(Guid.NewGuid(), athlete, Data(Now, Now.AddMinutes(30)), Now);
        var id = session.Id;

        var change = session.Synchronize(Data(Now.AddDays(1), Now.AddDays(1).AddMinutes(45)), Now.AddHours(1));

        Assert.Equal(SchedulingChangeType.Rescheduled, change);
        Assert.Equal(id, session.Id);
        Assert.Equal(athlete, session.AthleteProfileId);
        Assert.Equal(45, session.DurationMinutes);
    }

    [Fact]
    public void Cancellation_is_idempotent_and_never_deletes_history()
    {
        var session = Session.Create(Guid.NewGuid(), Guid.NewGuid(), Data(Now, Now.AddMinutes(30)), Now);
        Assert.True(session.Cancel(Now.AddMinutes(1), "Changed plans"));
        Assert.False(session.Cancel(Now.AddMinutes(2), "Duplicate"));
        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.Equal("Changed plans", session.CancellationReason);
    }

    private static CalendlySessionData Data(DateTime start, DateTime end) => new(
        "https://api.calendly.com/scheduled_events/event", "https://api.calendly.com/invitees/invitee",
        "https://api.calendly.com/event_types/type", start, end, DeliveryType.Online,
        "Zoom", "https://zoom.test/meeting", "https://calendly.test/cancel", "https://calendly.test/reschedule");
}

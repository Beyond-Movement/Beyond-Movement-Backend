using BeyondMovement.Modules.Scheduling.Domain;

namespace BeyondMovement.UnitTests.Scheduling;

/// <summary>
/// BR-04 to BR-07 as they are decided on the session itself. The deduction they cause is
/// covered by <see cref="Packages.PurchasedPackageTests"/>; here the question is only how much a
/// session says it should take, and which sessions may be resolved at all.
/// </summary>
public sealed class AttendanceTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Admin = Guid.NewGuid();

    [Fact]
    public void Booking_alone_consumes_nothing()
    {
        var session = Booked(60);

        // BR-04. A scheduled session has taken nothing and says so.
        Assert.Equal(SessionStatus.Scheduled, session.Status);
        Assert.Equal(0, session.ConsumedSessionCount);
        Assert.Equal(0, session.ConsumptionFor(SessionStatus.Scheduled, noShowDeducts: false));
    }

    [Fact]
    public void Attending_an_ordinary_session_consumes_one()
    {
        var session = Booked(60);

        Assert.Equal(1, session.ConsumptionFor(SessionStatus.Attended, noShowDeducts: false));

        Assert.True(session.Resolve(SessionStatus.Attended, 1, Admin, Now).IsSuccess);
        Assert.Equal(SessionStatus.Attended, session.Status);
        Assert.Equal(1, session.ConsumedSessionCount);
        Assert.Equal(Now, session.AttendedAtUtc);
        Assert.Equal(Admin, session.AttendedByUserId);
    }

    [Theory]
    [InlineData(30, true, 1)]
    [InlineData(30, false, 0)]
    [InlineData(60, true, 1)]     // the old rule said 0 here; the Admin's choice now decides
    [InlineData(90, false, 0)]    // and 1 here
    [InlineData(240, true, 1)]
    public void Observation_consumes_what_the_Admin_chose_whatever_its_duration(
        int minutes, bool deducts, int expected)
    {
        // BR-07. Every pair below crosses the old hour threshold in one direction or the other,
        // which is the point: duration no longer has a vote.
        var session = Session.CreateObservation(
            Guid.NewGuid(), Guid.NewGuid(), Now, Now.AddMinutes(minutes), "Regional final",
            deductsSession: deducts, Now);

        Assert.Equal(DeliveryType.Observation, session.DeliveryType);
        Assert.Equal(deducts, session.ObservationDeductsSession);
        Assert.Equal(expected, session.ConsumptionFor(SessionStatus.Attended, noShowDeducts: false));
    }

    [Fact]
    public void Recording_an_observation_that_will_deduct_still_deducts_nothing_yet()
    {
        var session = Session.CreateObservation(
            Guid.NewGuid(), Guid.NewGuid(), Now, Now.AddMinutes(90), "Regional final",
            deductsSession: true, Now);

        // BR-04 outranks the choice: the decision is recorded, not applied. Only Resolve applies it.
        Assert.Equal(SessionStatus.Scheduled, session.Status);
        Assert.Equal(0, session.ConsumedSessionCount);
        Assert.True(session.ObservationDeductsSession);
    }

    [Fact]
    public void An_observation_may_be_recorded_for_a_date_still_to_come()
    {
        var future = Now.AddDays(4);

        var session = Session.CreateObservation(
            Guid.NewGuid(), Guid.NewGuid(), future, future.AddMinutes(90), "Tournament venue",
            deductsSession: true, Now);

        // An observation is arranged directly with the athlete, so it may be recorded before it
        // happens. Nothing in the domain treats a future start differently.
        Assert.Equal(future, session.ScheduledStartUtc);
        Assert.Equal(SessionStatus.Scheduled, session.Status);
        Assert.Equal(1, session.ConsumptionFor(SessionStatus.Attended, noShowDeducts: false));
    }

    [Fact]
    public void An_ordinary_session_records_no_observation_choice()
    {
        // Null is what tells the app this session follows BR-05 and has no choice to show.
        Assert.Null(Booked(60).ObservationDeductsSession);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void No_show_deducts_only_when_the_deployment_says_so(bool policy, int expected)
    {
        // A-04: configurable, and nothing by default.
        Assert.Equal(expected, Booked(60).ConsumptionFor(SessionStatus.NoShow, policy));
    }

    [Fact]
    public void A_session_cannot_be_attended_twice()
    {
        var session = Booked(60);
        Assert.True(session.Resolve(SessionStatus.Attended, 1, Admin, Now).IsSuccess);

        var second = session.Resolve(SessionStatus.Attended, 1, Admin, Now.AddMinutes(1));

        // The guard that makes the deduction exactly-once: the second attempt never reaches a
        // package, so no second deduction is even possible.
        Assert.True(second.IsFailure);
        Assert.Equal("SESSION_ALREADY_ATTENDED", second.Error!.Code);
        Assert.Equal(1, session.ConsumedSessionCount);
    }

    [Fact]
    public void A_no_show_cannot_then_be_marked_attended()
    {
        var session = Booked(60);
        Assert.True(session.Resolve(SessionStatus.NoShow, 0, Admin, Now).IsSuccess);

        var second = session.Resolve(SessionStatus.Attended, 1, Admin, Now.AddMinutes(1));

        Assert.True(second.IsFailure);
        Assert.Equal("SESSION_ALREADY_RESOLVED", second.Error!.Code);
    }

    [Fact]
    public void A_no_show_records_no_attendance_stamp()
    {
        var session = Booked(60);

        Assert.True(session.Resolve(SessionStatus.NoShow, 0, Admin, Now).IsSuccess);

        Assert.Equal(SessionStatus.NoShow, session.Status);
        Assert.Null(session.AttendedAtUtc);
        Assert.Null(session.AttendedByUserId);
    }

    [Fact]
    public void A_cancelled_session_cannot_be_attended()
    {
        var session = Booked(60);
        Assert.True(session.Cancel(Now, "Athlete unwell"));

        var result = session.Resolve(SessionStatus.Attended, 1, Admin, Now.AddHours(1));

        // BR-06.
        Assert.True(result.IsFailure);
        Assert.Equal("SESSION_CANCELLED", result.Error!.Code);
        Assert.Equal(0, session.ConsumedSessionCount);
    }

    [Fact]
    public void Cancelling_never_reverses_a_session_that_was_already_attended()
    {
        var session = Booked(60);
        Assert.True(session.Resolve(SessionStatus.Attended, 1, Admin, Now).IsSuccess);

        // Consumed value is never given back silently. The cancellation is refused outright
        // rather than leaving a Cancelled session that still says it consumed one.
        Assert.False(session.Cancel(Now.AddHours(1), "Cancelled in Calendly afterwards"));
        Assert.Equal(SessionStatus.Attended, session.Status);
        Assert.Equal(1, session.ConsumedSessionCount);
    }

    [Fact]
    public void A_late_Calendly_update_does_not_return_an_attended_session_to_scheduled()
    {
        var session = Booked(60);
        Assert.True(session.Resolve(SessionStatus.Attended, 1, Admin, Now).IsSuccess);

        session.Synchronize(Data(Now.AddDays(1), Now.AddDays(1).AddMinutes(90)), Now.AddHours(2));

        // The new time is taken; the attendance is not undone. Otherwise a webhook arriving out
        // of order would strand a deduction against a session that looks unattended.
        Assert.Equal(SessionStatus.Attended, session.Status);
        Assert.Equal(1, session.ConsumedSessionCount);
        Assert.Equal(90, session.DurationMinutes);
    }

    [Fact]
    public void An_observation_carries_no_Calendly_identity()
    {
        var session = Session.CreateObservation(
            Guid.NewGuid(), Guid.NewGuid(), Now, Now.AddMinutes(90), "Club championship",
            deductsSession: true, Now);

        Assert.Null(session.CalendlyEventUri);
        Assert.Null(session.CalendlyInviteeUri);
        Assert.Null(session.CalendlyEventTypeUri);
        Assert.Equal(SessionStatus.Scheduled, session.Status);
    }

    [Fact]
    public void Resolving_to_a_status_that_is_not_an_outcome_is_a_caller_bug()
    {
        var session = Booked(60);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Resolve(SessionStatus.Cancelled, 0, Admin, Now));
    }

    private static Session Booked(int minutes) =>
        Session.Create(Guid.NewGuid(), Guid.NewGuid(), Data(Now, Now.AddMinutes(minutes)), Now);

    private static CalendlySessionData Data(DateTime start, DateTime end) => new(
        "https://api.calendly.com/scheduled_events/event", "https://api.calendly.com/invitees/invitee",
        "https://api.calendly.com/event_types/type", start, end, DeliveryType.Online,
        "Zoom", "https://zoom.test/meeting", "https://calendly.test/cancel", "https://calendly.test/reschedule");
}

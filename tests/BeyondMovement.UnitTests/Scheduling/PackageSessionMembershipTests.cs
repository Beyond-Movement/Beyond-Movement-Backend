using BeyondMovement.Modules.Scheduling.Domain;

namespace BeyondMovement.UnitTests.Scheduling;

/// <summary>
/// Which sessions belong to a purchased package, decided on the session itself.
/// <para>
/// <c>GET /packages/{packageId}/sessions</c> is a query over <c>Session.PackageId</c>, and it is
/// only a correct answer to "what was this package spent on?" because that column is written
/// <b>exactly when something was deducted</b> and never otherwise. These pin that rule where it
/// lives, so the endpoint's meaning cannot be changed by a later edit to the domain without a
/// test going red.
/// </para>
/// <para>
/// How much a session decides to consume is <see cref="AttendanceTests"/>'s subject; here the
/// question is only what that decision does to the package link.
/// </para>
/// </summary>
public sealed class PackageSessionMembershipTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Package = Guid.NewGuid();

    [Fact]
    public void A_scheduled_session_belongs_to_no_package()
    {
        var session = Booked(60);

        // BR-04. Nothing has been taken, so there is nothing for the list to report.
        Assert.Null(session.PackageId);
        Assert.Equal(0, session.ConsumedSessionCount);
        Assert.Null(session.ConsumedPackagePosition);
    }

    [Fact]
    public void A_cancelled_session_belongs_to_no_package()
    {
        var session = Booked(60);

        Assert.True(session.Cancel(Now.AddMinutes(1), "Changed plans"));

        // BR-06. Cancelling never deducts, so a cancelled session was never spent on.
        Assert.Null(session.PackageId);
        Assert.Equal(0, session.ConsumedSessionCount);
    }

    [Fact]
    public void An_attended_session_is_attached_to_the_package_it_consumed()
    {
        var session = Booked(60);

        Assert.True(session.Resolve(SessionStatus.Attended, 1, Admin, Now).IsSuccess);
        session.AttachToPackage(Package, consumedPackagePosition: 3);

        Assert.Equal(Package, session.PackageId);
        Assert.Equal(1, session.ConsumedSessionCount);
        Assert.Equal(3, session.ConsumedPackagePosition);
    }

    [Fact]
    public void A_no_show_the_coach_charged_is_attached_like_any_other_deduction()
    {
        var session = Booked(60);

        Assert.True(session.Resolve(SessionStatus.NoShow, 1, Admin, Now).IsSuccess);
        session.AttachToPackage(Package, consumedPackagePosition: 1);

        // It is in the list because the coach chose to charge it - that is what makes it part of
        // what the package was spent on, even though nobody turned up.
        Assert.Equal(Package, session.PackageId);
        Assert.Equal(SessionStatus.NoShow, session.Status);
        Assert.Equal(1, session.ConsumedPackagePosition);

        // And it carries no attendance stamp, which is what the list reports as a null.
        Assert.Null(session.AttendedAtUtc);
    }

    [Fact]
    public void A_deducting_observation_is_attached_like_any_other_deduction()
    {
        var session = Session.CreateObservation(Guid.NewGuid(), Guid.NewGuid(), Now,
            Now.AddMinutes(90), "Regional final", deductsSession: true, Now);

        Assert.Equal(1, session.ConsumptionFor(SessionStatus.Attended, noShowDeducts: false));
        Assert.True(session.Resolve(SessionStatus.Attended, 1, Admin, Now).IsSuccess);
        session.AttachToPackage(Package, consumedPackagePosition: 2);

        // An observation is one delivery type, not a separate kind of history. BR-07 decided
        // whether it deducts; having deducted, it belongs to the package like anything else.
        Assert.Equal(Package, session.PackageId);
        Assert.Equal(DeliveryType.Observation, session.DeliveryType);
        Assert.Equal(2, session.ConsumedPackagePosition);
    }

    [Theory]
    [InlineData(SessionStatus.NoShow)]
    [InlineData(SessionStatus.Attended)]
    public void A_resolved_session_that_consumed_nothing_cannot_be_attached_to_a_package(
        SessionStatus outcome)
    {
        // A non-deducting observation when Attended, and a no-show the coach chose not to charge
        // when NoShow. Both happened, and both took nothing.
        var session = Session.CreateObservation(Guid.NewGuid(), Guid.NewGuid(), Now,
            Now.AddMinutes(30), "Club training", deductsSession: false, Now);

        Assert.Equal(0, session.ConsumptionFor(SessionStatus.Attended, noShowDeducts: false));
        Assert.True(session.Resolve(outcome, 0, Admin, Now).IsSuccess);

        // The guard that keeps the list honest. Attaching it would put a session in the package's
        // history that the package never paid for, and the count would stop matching usedSessions.
        Assert.Throws<InvalidOperationException>(() => session.AttachToPackage(Package, 1));

        Assert.Null(session.PackageId);
        Assert.Null(session.ConsumedPackagePosition);
    }

    [Fact]
    public void A_position_is_one_based_so_no_deduction_can_be_recorded_as_the_zeroth()
    {
        var session = Booked(60);
        Assert.True(session.Resolve(SessionStatus.Attended, 1, Admin, Now).IsSuccess);

        // The list promises a one-based "Session N", and CK_Sessions_PackagePositionMatchesConsumption
        // requires it at the database. This is the same rule refusing in code, before it gets there.
        Assert.Throws<ArgumentOutOfRangeException>(() => session.AttachToPackage(Package, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.AttachToPackage(Package, -1));
    }

    private static Session Booked(int minutes) =>
        Session.Create(Guid.NewGuid(), Guid.NewGuid(), Data(Now, Now.AddMinutes(minutes)), Now);

    private static CalendlySessionData Data(DateTime start, DateTime end) => new(
        "https://api.calendly.com/scheduled_events/event", "https://api.calendly.com/invitees/invitee",
        "https://api.calendly.com/event_types/type", start, end, DeliveryType.Online,
        "Zoom", "https://zoom.test/meeting", "https://calendly.test/cancel", "https://calendly.test/reschedule");
}

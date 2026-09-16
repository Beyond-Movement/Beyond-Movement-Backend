using BeyondMovement.Modules.Scheduling.Domain;

namespace BeyondMovement.UnitTests.Scheduling;

/// <summary>
/// The Observation Request state machine, decided on the entity itself. Who is allowed to call
/// these methods is an endpoint question and is covered by the integration suite; here the only
/// question is which transitions the request permits at all.
/// <para>
/// The whole rule is one sentence — <b>Pending is the only state anything may be done from</b> —
/// and it is worth a test per verb because each one is a separate guard that a later change
/// could drop independently.
/// </para>
/// </summary>
public sealed class ObservationRequestTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 9, 20, 14, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Athlete = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    [Fact]
    public void A_new_request_is_pending_and_has_produced_nothing()
    {
        var request = Pending();

        Assert.Equal(ObservationRequestStatus.Pending, request.Status);
        Assert.Null(request.SessionId);
        Assert.Null(request.ResolvedAtUtc);
        Assert.Null(request.ResolvedByUserId);
        Assert.Equal(Now, request.CreatedAtUtc);
    }

    [Fact]
    public void The_end_follows_from_the_duration()
    {
        var request = Pending(durationMinutes: 90);

        Assert.Equal(Start.AddMinutes(90), request.RequestedEndUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_details_are_stored_as_null(string? details)
    {
        var request = ObservationRequest.Create(
            Guid.NewGuid(), Athlete, Start, 60, "Cairo Stadium", details, Now);

        // Not "", so a cleared box does not come back as a blank line the app would render.
        Assert.Null(request.Details);
    }

    [Fact]
    public void Location_and_details_are_trimmed()
    {
        var request = ObservationRequest.Create(
            Guid.NewGuid(), Athlete, Start, 60, "  Cairo Stadium  ", "  National final  ", Now);

        Assert.Equal("Cairo Stadium", request.Location);
        Assert.Equal("National final", request.Details);
    }

    [Fact]
    public void A_pending_request_can_be_revised()
    {
        var request = Pending();
        var later = Start.AddDays(1);

        var revised = request.Revise(later, 120, "Alexandria Sports Hall", "Regional heats", Now.AddHours(1));

        Assert.True(revised.IsSuccess);
        Assert.Equal(later, request.RequestedStartUtc);
        Assert.Equal(120, request.RequestedDurationMinutes);
        Assert.Equal("Alexandria Sports Hall", request.Location);
        Assert.Equal("Regional heats", request.Details);
        Assert.Equal(Now.AddHours(1), request.UpdatedAtUtc);

        // Still waiting on the coach: revising is not a decision.
        Assert.Equal(ObservationRequestStatus.Pending, request.Status);
        Assert.Null(request.ResolvedAtUtc);
    }

    [Fact]
    public void Accepting_records_the_session_it_produced()
    {
        var request = Pending();
        var sessionId = Guid.NewGuid();

        var accepted = request.Accept(sessionId, Admin, Now);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(ObservationRequestStatus.Accepted, request.Status);
        Assert.Equal(sessionId, request.SessionId);
        Assert.Equal(Admin, request.ResolvedByUserId);
        Assert.Equal(Now, request.ResolvedAtUtc);
    }

    [Theory]
    [InlineData(ObservationRequestStatus.Declined)]
    [InlineData(ObservationRequestStatus.Cancelled)]
    public void Declining_and_cancelling_produce_no_session(ObservationRequestStatus outcome)
    {
        var request = Pending();

        var resolved = outcome == ObservationRequestStatus.Declined
            ? request.Decline(Admin, Now)
            : request.Cancel(Athlete, Now);

        Assert.True(resolved.IsSuccess);
        Assert.Equal(outcome, request.Status);
        Assert.Null(request.SessionId);
        Assert.Equal(Now, request.ResolvedAtUtc);
    }

    [Theory]
    [InlineData(ObservationRequestStatus.Accepted)]
    [InlineData(ObservationRequestStatus.Declined)]
    [InlineData(ObservationRequestStatus.Cancelled)]
    public void Nothing_can_be_revised_once_it_has_left_pending(ObservationRequestStatus resolved)
    {
        var request = Resolved(resolved);

        var revised = request.Revise(Start.AddDays(2), 45, "Somewhere else", "Changed my mind", Now);

        Assert.True(revised.IsFailure);
        Assert.Equal("OBSERVATION_REQUEST_NOT_PENDING", revised.Error!.Code);

        // Refused, not partially applied: the record of what was agreed is untouched.
        Assert.Equal(Start, request.RequestedStartUtc);
        Assert.Equal("Cairo Stadium", request.Location);
    }

    [Theory]
    [InlineData(ObservationRequestStatus.Accepted)]
    [InlineData(ObservationRequestStatus.Declined)]
    [InlineData(ObservationRequestStatus.Cancelled)]
    public void Nothing_can_be_cancelled_once_it_has_left_pending(ObservationRequestStatus resolved)
    {
        var request = Resolved(resolved);

        var cancelled = request.Cancel(Athlete, Now);

        Assert.True(cancelled.IsFailure);
        Assert.Equal("OBSERVATION_REQUEST_NOT_PENDING", cancelled.Error!.Code);
        Assert.Equal(resolved, request.Status);
    }

    [Theory]
    [InlineData(ObservationRequestStatus.Accepted)]
    [InlineData(ObservationRequestStatus.Declined)]
    [InlineData(ObservationRequestStatus.Cancelled)]
    public void Nothing_can_be_declined_once_it_has_left_pending(ObservationRequestStatus resolved)
    {
        var request = Resolved(resolved);

        var declined = request.Decline(Admin, Now);

        Assert.True(declined.IsFailure);
        Assert.Equal("OBSERVATION_REQUEST_NOT_PENDING", declined.Error!.Code);
        Assert.Equal(resolved, request.Status);
    }

    /// <summary>
    /// The guard that makes a repeated acceptance produce one session rather than two. The
    /// endpoint and the row lock in front of it are the first two layers; this is the third, and
    /// the one that survives a change to either.
    /// </summary>
    [Fact]
    public void A_second_acceptance_is_refused_and_keeps_the_first_session()
    {
        var request = Pending();
        var first = Guid.NewGuid();
        Assert.True(request.Accept(first, Admin, Now).IsSuccess);

        var second = request.Accept(Guid.NewGuid(), Admin, Now.AddMinutes(1));

        Assert.True(second.IsFailure);
        Assert.Equal("OBSERVATION_REQUEST_NOT_PENDING", second.Error!.Code);
        Assert.Equal(first, request.SessionId);
        Assert.Equal(Now, request.ResolvedAtUtc);
    }

    [Fact]
    public void An_acceptance_without_a_session_is_a_caller_bug_not_a_failure()
    {
        var request = Pending();

        // Thrown rather than returned: no request can produce this, and a Result would invite a
        // caller to handle an accepted request that points at nothing.
        Assert.Throws<ArgumentOutOfRangeException>(() => request.Accept(Guid.Empty, Admin, Now));
        Assert.Equal(ObservationRequestStatus.Pending, request.Status);
    }

    [Fact]
    public void A_non_utc_start_is_converted_rather_than_stored_as_given()
    {
        var local = new DateTime(2026, 9, 20, 14, 0, 0, DateTimeKind.Utc).ToLocalTime();

        var request = ObservationRequest.Create(
            Guid.NewGuid(), Athlete, local, 60, "Cairo Stadium", null, Now);

        Assert.Equal(DateTimeKind.Utc, request.RequestedStartUtc.Kind);
        Assert.Equal(Start, request.RequestedStartUtc);
    }

    private static ObservationRequest Pending(int durationMinutes = 60) =>
        ObservationRequest.Create(
            Guid.NewGuid(), Athlete, Start, durationMinutes, "Cairo Stadium", "National final", Now);

    private static ObservationRequest Resolved(ObservationRequestStatus outcome)
    {
        var request = Pending();

        var result = outcome switch
        {
            ObservationRequestStatus.Accepted => request.Accept(Guid.NewGuid(), Admin, Now),
            ObservationRequestStatus.Declined => request.Decline(Admin, Now),
            ObservationRequestStatus.Cancelled => request.Cancel(Athlete, Now),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Pending is not a resolved state.")
        };

        Assert.True(result.IsSuccess);
        return request;
    }
}

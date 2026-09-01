using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Athletes.Domain;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Scheduling.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeyondMovement.IntegrationTests;

/// <summary>A clock the test drives, so "this week" is the same week on every run.</summary>
public sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Phase 9. Seeds a fixed set of sessions around a <b>pinned "now"</b>, so every period boundary
/// is exact and the expected counts can be written down rather than computed.
/// <para>
/// Without a fixed clock these tests would assert different windows depending on the day they
/// ran, and "weekly" would be empty every Monday morning. The pinned instant is a Thursday, so
/// the current week has days on both sides of it.
/// </para>
/// </summary>
public sealed class DashboardApiFactory : ApiFactory
{
    /// <summary>Thursday 12 March 2026, 12:00 UTC. Cairo is UTC+2 on this date.</summary>
    public static readonly DateTime Now = new(2026, 3, 12, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The coach's zone. The whole point of the period maths is that this is honoured.</summary>
    public const string CoachTimeZone = "Africa/Cairo";

    public FixedClock Clock { get; } = new() { UtcNow = Now };

    public Guid AthleteUserId { get; private set; }
    public string AthleteEmail => "dash-athlete@nowhere.test";

    /// <summary>Ids of the three sessions that should appear, in order, under the default limit.</summary>
    public Guid[] ExpectedUpcoming { get; private set; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);

            // Tokens are stamped from IClock, but the bearer middleware checks expiry against
            // the real system clock - so a token minted at the pinned instant is already
            // expired the moment it is issued, and every request 401s. Lifetime validation is
            // the only thing that disagrees; switching it off here keeps the rest of the auth
            // path real. Test-host only: nothing in the application changes.
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options => options.TokenValidationParameters.ValidateLifetime = false);
        });
    }

    protected override async Task InitializeCoreAsync()
    {
        await base.InitializeCoreAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var coach = await db.Users.SingleAsync(x => x.Role == UserRole.Admin);

        // The coach works in Cairo. Every period boundary below is a Cairo midnight.
        await db.Database.ExecuteSqlAsync(
            $"""update "Users" set "TimeZone" = {CoachTimeZone} where "Id" = {coach.Id}""");

        AthleteUserId = await AthleteApiFactory.AddAthleteAsync(
            db, scope.ServiceProvider, AthleteEmail, "Dash Athlete", "Tennis",
            new DateOnly(2000, 1, 1), Now.AddYears(-1));

        var profileId = await db.AthleteProfiles
            .Where(x => x.UserId == AthleteUserId).Select(x => x.Id).SingleAsync();

        // --- delivered work, spread so each period includes a different subset ---------------
        // Cairo week of the pinned Thursday runs Mon 9 Mar 00:00 to Mon 16 Mar 00:00 local,
        // which is 8 Mar 22:00 UTC to 15 Mar 22:00 UTC.

        Attended(db, coach.Id, profileId, "wk-online", new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc), 60, DeliveryType.Online);
        Attended(db, coach.Id, profileId, "wk-f2f", new DateTime(2026, 3, 11, 10, 0, 0, DateTimeKind.Utc), 90, DeliveryType.FaceToFace);

        // Earlier the same month, outside the week.
        Attended(db, coach.Id, profileId, "mo-obs", new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc), 45, DeliveryType.Observation);

        // Earlier the same year, outside the month.
        Attended(db, coach.Id, profileId, "yr-online", new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc), 30, DeliveryType.Online);

        // Last year: all-time only.
        Attended(db, coach.Id, profileId, "all-online", new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Utc), 120, DeliveryType.Online);

        // --- things that happened this week but were NOT delivered --------------------------
        // Both sit inside every window, so if either is ever counted the weekly numbers move.
        Resolved(db, coach.Id, profileId, "wk-noshow", new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc), 60, DeliveryType.Online, SessionStatus.NoShow);
        Cancelled(db, coach.Id, profileId, "wk-cancelled", new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc), 60, DeliveryType.Online);

        // Still scheduled, in the past: not delivered either.
        Scheduled(db, coach.Id, profileId, "wk-unresolved", new DateTime(2026, 3, 10, 16, 0, 0, DateTimeKind.Utc), 60, DeliveryType.Online);

        // --- another coach's delivered session, which must never be visible -----------------
        var foreignCoachId = Guid.NewGuid();
        var foreignProfileId = await AddForeignAthleteAsync(db, scope.ServiceProvider, foreignCoachId);
        Attended(db, foreignCoachId, foreignProfileId, "foreign", new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc), 600, DeliveryType.Online);

        // --- upcoming: four scheduled ahead, plus a cancelled one that must not appear -------
        var first = Scheduled(db, coach.Id, profileId, "up-1", Now.AddDays(1), 60, DeliveryType.Online);
        var second = Scheduled(db, coach.Id, profileId, "up-2", Now.AddDays(2), 60, DeliveryType.FaceToFace);
        var third = Scheduled(db, coach.Id, profileId, "up-3", Now.AddDays(3), 60, DeliveryType.Observation);
        Scheduled(db, coach.Id, profileId, "up-4", Now.AddDays(4), 60, DeliveryType.Online);
        Cancelled(db, coach.Id, profileId, "up-cancelled", Now.AddHours(6), 60, DeliveryType.Online);

        ExpectedUpcoming = [first, second, third];

        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddForeignAthleteAsync(
        AppDbContext db, IServiceProvider services, Guid coachId)
    {
        var hasher = services.GetRequiredService<IPasswordHasher<User>>();
        var user = User.CreateAthlete("dash-foreign@nowhere.test", "Foreign", "placeholder", null, coachId, Now);
        user.SetPasswordHash(hasher.HashPassword(user, AthleteApiFactory.AthletePassword), Now);
        db.Users.Add(user);

        var profile = AthleteProfile.CreateEmpty(user.Id, coachId, Now);
        db.AthleteProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    private static Guid Scheduled(
        AppDbContext db, Guid coachId, Guid profileId, string tag,
        DateTime startUtc, int minutes, DeliveryType type)
    {
        var session = type == DeliveryType.Observation
            ? Session.CreateObservation(coachId, profileId, startUtc, startUtc.AddMinutes(minutes), null, false, Now)
            : Session.Create(coachId, profileId, new CalendlySessionData(
                $"https://calendly.test/e/{tag}-{Guid.NewGuid():N}",
                $"https://calendly.test/i/{tag}",
                "https://calendly.test/t/standard",
                startUtc, startUtc.AddMinutes(minutes), type, null, null, null, null), Now);

        db.Sessions.Add(session);
        return session.Id;
    }

    private static Guid Attended(
        AppDbContext db, Guid coachId, Guid profileId, string tag,
        DateTime startUtc, int minutes, DeliveryType type) =>
        Resolved(db, coachId, profileId, tag, startUtc, minutes, type, SessionStatus.Attended);

    /// <summary>
    /// Marks the session through <c>Session.Resolve</c> rather than writing the column, so these
    /// rows are in exactly the state the real attendance path produces. Nothing is deducted -
    /// the package balance is Phase 6's concern and irrelevant to a count of delivered work.
    /// </summary>
    private static Guid Resolved(
        AppDbContext db, Guid coachId, Guid profileId, string tag,
        DateTime startUtc, int minutes, DeliveryType type, SessionStatus outcome)
    {
        var id = Scheduled(db, coachId, profileId, tag, startUtc, minutes, type);
        var session = db.Sessions.Local.Single(x => x.Id == id);
        session.Resolve(outcome, 0, coachId, startUtc.AddMinutes(minutes));
        return id;
    }

    private static Guid Cancelled(
        AppDbContext db, Guid coachId, Guid profileId, string tag,
        DateTime startUtc, int minutes, DeliveryType type)
    {
        var id = Scheduled(db, coachId, profileId, tag, startUtc, minutes, type);
        db.Sessions.Local.Single(x => x.Id == id).Cancel(startUtc, "test");
        return id;
    }
}

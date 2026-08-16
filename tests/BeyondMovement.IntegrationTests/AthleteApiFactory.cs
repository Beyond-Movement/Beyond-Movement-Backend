using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Athletes.Domain;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// An <see cref="ApiFactory"/> with a known set of athletes, so list, search, filter and sort
/// assertions have something stable to run against. Its own fixture, so these rows cannot
/// disturb the auth and invitation suites.
/// </summary>
public sealed class AthleteApiFactory : ApiFactory
{
    public const string AthletePassword = "Athlete#Strong2026";
    public const int SeededAthletes = 4;

    /// <summary>An athlete belonging to a different coach. Must be invisible to the seeded Admin.</summary>
    public static Guid ForeignAthleteId { get; private set; }

    protected override async Task InitializeCoreAsync()
    {
        await base.InitializeCoreAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Creation times are spread so "newly added" and "oldest added" have a real order.
        var baseTime = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

        await AddAthleteAsync(db, scope.ServiceProvider, "alex@nowhere.test", "Alex Thompson",
            "Tennis", new DateOnly(2001, 4, 17), baseTime, gender: Gender.Female);

        await AddAthleteAsync(db, scope.ServiceProvider, "jordan@nowhere.test", "Jordan Blake",
            "Swimming", new DateOnly(1999, 2, 11), baseTime.AddDays(1));

        await AddAthleteAsync(db, scope.ServiceProvider, "sam@nowhere.test", "Sam Reed",
            "Athletics", new DateOnly(2000, 6, 3), baseTime.AddDays(2), status: UserStatus.Paused);

        // Registered but never finished Complete Profile, so no sport and no date of birth.
        // Proves sport-sorting puts blanks last rather than first, and that the coach can still
        // see an athlete who has not filled anything in.
        await AddAthleteAsync(db, scope.ServiceProvider, "robin@nowhere.test", "Robin Vale",
            sport: null, dateOfBirth: null, baseTime.AddDays(3));

        ForeignAthleteId = await AddAthleteAsync(db, scope.ServiceProvider,
            "foreign@nowhere.test", "Foreign Athlete", "Cycling", null, baseTime,
            coachId: Guid.NewGuid());
    }

    public static async Task<Guid> AddAthleteAsync(
        AppDbContext db,
        IServiceProvider services,
        string email,
        string fullName,
        string? sport,
        DateOnly? dateOfBirth,
        DateTime? createdAtUtc = null,
        UserStatus status = UserStatus.Active,
        Gender? gender = null,
        Guid? coachId = null)
    {
        var hasher = services.GetRequiredService<IPasswordHasher<User>>();
        var clock = services.GetRequiredService<IClock>();

        var owner = coachId ?? (await db.Users.AsNoTracking().SingleAsync(u => u.Role == UserRole.Admin)).Id;
        var created = createdAtUtc ?? clock.UtcNow;

        var user = User.CreateAthlete(email, fullName, "placeholder", null, owner, created);
        user.SetPasswordHash(hasher.HashPassword(user, AthletePassword), created);

        if (status == UserStatus.Paused)
            user.Pause(created);

        db.Users.Add(user);

        var profile = AthleteProfile.CreateEmpty(user.Id, owner, created);

        // Complete Profile is all-or-nothing, so the fixture is too: an athlete missing any
        // detail is one who never finished it, and must not read as completed.
        if (sport is not null && dateOfBirth is not null)
        {
            profile.CompleteProfile(dateOfBirth.Value, gender ?? Gender.Female, sport, created);
            user.MarkProfileCompleted(created);
        }

        db.AthleteProfiles.Add(profile);

        await db.SaveChangesAsync();

        // CreatedAtUtc is assigned inside the factory method and has no setter, so the spread
        // of creation times is applied afterwards.
        await db.Database.ExecuteSqlAsync(
            $"""update "Users" set "CreatedAtUtc" = {created} where "Id" = {user.Id}""");

        return user.Id;
    }
}

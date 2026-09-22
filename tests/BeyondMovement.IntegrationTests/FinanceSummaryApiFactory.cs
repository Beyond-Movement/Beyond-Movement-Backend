using BeyondMovement.Infrastructure;
using BeyondMovement.Modules.Finance.Domain;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// A fixed financial history around a <b>pinned "now"</b>, so every period boundary is exact and
/// the expected totals can be written down rather than computed.
/// <para>
/// Without a fixed clock these tests would assert different windows depending on the day they
/// ran, and "this week" would be empty every Monday morning. The same arrangement
/// <see cref="DashboardApiFactory"/> uses, and for the same reason.
/// </para>
/// <para>
/// <b>Nothing here goes through the HTTP API.</b> Income has to be dated in the past, and the
/// only way to pay a purchase through the API is to pay it now. The rows are built with the real
/// domain factories — <see cref="PackagePurchase.RecordAdminSale"/>,
/// <see cref="PackagePurchase.Select"/>, <see cref="Expense.Record"/> — so they are exactly what
/// the application produces, and only the timestamps are then moved.
/// </para>
/// </summary>
public sealed class FinanceSummaryApiFactory : ApiFactory
{
    /// <summary>Thursday 12 March 2026, 12:00 UTC. Cairo is UTC+2 on this date.</summary>
    public static readonly DateTime Now = new(2026, 3, 12, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The coach's zone. The whole point of the period maths is that this is honoured.</summary>
    public const string CoachTimeZone = "Africa/Cairo";

    public FixedClock Clock { get; } = new() { UtcNow = Now };

    // The windows the pinned instant produces, in UTC. Cairo midnight is 22:00 UTC the day
    // before, which is exactly the shift these tests exist to prove is applied.
    //   Weekly  [2026-03-08 22:00Z, 2026-03-15 22:00Z)   Mon 9 Mar - Mon 16 Mar, Cairo
    //   Monthly [2026-02-28 22:00Z, 2026-03-31 22:00Z)   1 Mar - 1 Apr, Cairo
    //   Yearly  [2025-12-31 22:00Z, 2026-12-31 22:00Z)   1 Jan 2026 - 1 Jan 2027, Cairo

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);

            // Tokens are stamped from IClock, but the bearer middleware checks expiry against the
            // real system clock, so a token minted at the pinned instant is already expired.
            // Test-host only: nothing in the application changes.
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

        // The coach works in Cairo. Every boundary below is a Cairo midnight.
        await db.Database.ExecuteSqlAsync(
            $"""update "Users" set "TimeZone" = {CoachTimeZone} where "Id" = {coach.Id}""");

        // --- income: paid purchases, dated by when the money arrived ------------------------
        // One athlete holds all of them; each package is closed as it is created, because BR-03
        // allows only one active package per athlete and the income is on the purchase anyway.

        var payer = await AddAthleteAsync(db, scope.ServiceProvider, coach.Id, "finance-payer");

        await PaidAsync(db, coach.Id, payer, 400_000, new DateTime(2026, 3, 12, 10, 0, 0, DateTimeKind.Utc));
        await PaidAsync(db, coach.Id, payer, 300_000, new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc));
        await PaidAsync(db, coach.Id, payer, 200_000, new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));
        await PaidAsync(db, coach.Id, payer, 100_000, new DateTime(2025, 6, 1, 10, 0, 0, DateTimeKind.Utc));

        // A comped package. Paid, worth nothing, and a real transaction all the same: it adds one
        // to the count and nothing to the total.
        await PaidAsync(db, coach.Id, payer, 0, new DateTime(2026, 3, 12, 11, 0, 0, DateTimeKind.Utc));

        // --- outstanding: pending purchases, dated by when they were asked for ---------------
        // One pending purchase per athlete is a filtered unique index, so these need two.

        var owesThisWeek = await AddAthleteAsync(db, scope.ServiceProvider, coach.Id, "finance-owes-now");
        var owesLongAgo = await AddAthleteAsync(db, scope.ServiceProvider, coach.Id, "finance-owes-old");

        // A real catalogue entry, because a pending purchase carries a non-null PackageOptionId
        // with a foreign key behind it. A paid one may carry null and does.
        var option = PackageOption.Create(
            coach.Id, "Finance fixture option", 8, 400_000,
            [new PackageFeature("Weekly video call", null)], Now);

        db.PackageOptions.Add(option);
        await db.SaveChangesAsync();

        await PendingAsync(db, coach.Id, owesThisWeek, option.Id, 500_000, new DateTime(2026, 3, 11, 10, 0, 0, DateTimeKind.Utc));
        await PendingAsync(db, coach.Id, owesLongAgo, option.Id, 250_000, new DateTime(2025, 5, 1, 10, 0, 0, DateTimeKind.Utc));

        // --- expenses, dated by the day on the receipt ---------------------------------------
        // Each pair straddles one boundary, so a window that is off by a day fails a test.

        AddExpense(db, coach.Id, "This week", 500_000, new DateOnly(2026, 3, 12));
        AddExpense(db, coach.Id, "Week starts", 30_000, new DateOnly(2026, 3, 9));    // Monday, inclusive
        AddExpense(db, coach.Id, "Day before the week", 20_000, new DateOnly(2026, 3, 8));
        AddExpense(db, coach.Id, "Month starts", 10_000, new DateOnly(2026, 3, 1));   // inclusive
        AddExpense(db, coach.Id, "Day before the month", 5_000, new DateOnly(2026, 2, 28));
        AddExpense(db, coach.Id, "Year starts", 3_000, new DateOnly(2026, 1, 1));     // inclusive
        AddExpense(db, coach.Id, "Day before the year", 2_000, new DateOnly(2025, 12, 31));

        // --- another coach's money, which must never appear in any total ---------------------

        var foreignCoachId = Guid.NewGuid();
        var foreignAthlete = await AddAthleteAsync(
            db, scope.ServiceProvider, foreignCoachId, "finance-foreign");

        await PaidAsync(db, foreignCoachId, foreignAthlete, 9_000_000,
            new DateTime(2026, 3, 12, 9, 0, 0, DateTimeKind.Utc));

        AddExpense(db, foreignCoachId, "Not this coach's", 8_000_000, new DateOnly(2026, 3, 12));

        await db.SaveChangesAsync();
    }

    /// <summary>Reads the database directly, for assertions the API deliberately will not make.</summary>
    public async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<(Guid UserId, Guid ProfileId)> AddAthleteAsync(
        AppDbContext db, IServiceProvider services, Guid coachId, string handle)
    {
        var userId = await AthleteApiFactory.AddAthleteAsync(
            db, services, $"{handle}@nowhere.test", handle, "Tennis",
            new DateOnly(2000, 1, 1), Now.AddYears(-1), coachId: coachId);

        var profileId = await db.AthleteProfiles
            .Where(x => x.UserId == userId).Select(x => x.Id).SingleAsync();

        return (userId, profileId);
    }

    /// <summary>
    /// A package sold and paid for, then closed and backdated.
    /// <para>
    /// The purchase is built by <see cref="PackagePurchase.RecordAdminSale"/>, so it is born Paid
    /// with a real package behind it — the state <c>CK_PackagePurchases_PaidConsistency</c>
    /// requires. Only <c>PaidAtUtc</c> is then moved, because that is the one thing a test cannot
    /// ask the API for: the API can only pay a purchase now.
    /// </para>
    /// </summary>
    private static async Task PaidAsync(
        AppDbContext db, Guid coachId, (Guid UserId, Guid ProfileId) athlete, long priceMinor,
        DateTime paidAtUtc)
    {
        var package = PurchasedPackage.Purchase(
            coachId, athlete.ProfileId, packageOptionId: null, "Finance fixture", 8,
            [], priceMinor, DateOnly.FromDateTime(paidAtUtc), null, null, paidAtUtc);

        // Closed immediately: BR-03 allows one active package per athlete, and these all belong
        // to one. What the purchase records as received is unaffected by the package's status.
        package.Close(paidAtUtc);

        var purchase = PackagePurchase.RecordAdminSale(
            coachId, athlete.ProfileId, athlete.UserId, packageOptionId: null, "Finance fixture",
            8, [], priceMinor, Currency.Egp, package.Id, actorUserId: coachId, paidAtUtc);

        db.PurchasedPackages.Add(package);
        db.PackagePurchases.Add(purchase);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync(
            $"""
             update "PackagePurchases"
             set "PaidAtUtc" = {paidAtUtc}, "CreatedAtUtc" = {paidAtUtc}, "UpdatedAtUtc" = {paidAtUtc}
             where "Id" = {purchase.Id}
             """);
    }

    /// <summary>
    /// A purchase the athlete asked for and nobody has confirmed. Dated by
    /// <c>CreatedAtUtc</c>, because a pending purchase has no payment date — which is the whole
    /// of what makes it pending.
    /// </summary>
    private static async Task PendingAsync(
        AppDbContext db, Guid coachId, (Guid UserId, Guid ProfileId) athlete, Guid packageOptionId,
        long priceMinor, DateTime createdAtUtc)
    {
        var purchase = PackagePurchase.Select(
            coachId, athlete.ProfileId, athlete.UserId, packageOptionId,
            "Finance fixture pending", 8, [], priceMinor, Currency.Egp, createdAtUtc);

        db.PackagePurchases.Add(purchase);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync(
            $"""
             update "PackagePurchases"
             set "CreatedAtUtc" = {createdAtUtc}, "UpdatedAtUtc" = {createdAtUtc}
             where "Id" = {purchase.Id}
             """);
    }

    private static void AddExpense(
        AppDbContext db, Guid coachId, string title, long amountMinor, DateOnly incurredOn) =>
        db.Expenses.Add(Expense.Record(coachId, title, amountMinor, incurredOn, null, Now));
}

using BeyondMovement.Infrastructure;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Phase 8 purchases. Its own fixture, and InstaPay is deliberately left <b>unconfigured</b> here
/// so the 503 path is the default rather than something a test has to arrange —
/// <see cref="InstaPayApiFactory"/> covers the configured half.
/// <para>
/// Athletes are created per test rather than seeded up front. An athlete may hold only one
/// pending purchase and one active package, so tests that shared one would constrain each other
/// and fail in whatever order the runner happened to pick.
/// </para>
/// </summary>
public class PurchaseApiFactory : ApiFactory
{
    private int _athleteCounter;

    /// <summary>
    /// Blanks the InstaPay settings <b>explicitly</b>, rather than relying on there being none.
    /// <para>
    /// The Api project has a <c>UserSecretsId</c>, and the test host runs as Development, so a
    /// developer who has set real InstaPay values with <c>dotnet user-secrets</c> would otherwise
    /// have this fixture pick them up — and the 503 test would fail on their machine while
    /// passing in CI, where no user secrets exist. A test whose result depends on who is running
    /// it is worse than no test.
    /// </para>
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:InstaPay:PaymentUrl"] = string.Empty,
                ["Payments:InstaPay:QrImageUrl"] = string.Empty,
                ["Payments:InstaPay:RecipientName"] = string.Empty,
                ["Payments:InstaPay:RecipientHandle"] = string.Empty,
                ["Payments:InstaPay:Instructions:0"] = null,
                ["Payments:InstaPay:Instructions:1"] = null,
                ["Payments:InstaPay:Instructions:2"] = null
            }));
    }

    /// <summary>
    /// Creates an athlete nobody else is using, with a completed profile, and returns their user
    /// id and email. The email is unique per call so a test can log in as them.
    /// </summary>
    public async Task<(Guid UserId, string Email)> NewAthleteAsync(bool isLoyal = false)
    {
        var ordinal = Interlocked.Increment(ref _athleteCounter);
        var email = $"purchaser{ordinal}@nowhere.test";

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var userId = await AthleteApiFactory.AddAthleteAsync(
            db, scope.ServiceProvider, email, $"Purchaser {ordinal}", "Tennis",
            new DateOnly(2000, 1, 1));

        if (isLoyal)
        {
            var profile = await db.AthleteProfiles.SingleAsync(x => x.UserId == userId);
            profile.SetLoyalty(true, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        return (userId, email);
    }

    /// <summary>Reads the database directly, for assertions the API deliberately will not make.</summary>
    public async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}

/// <summary>
/// The same application with the coach's InstaPay details supplied, so the configured shape of
/// <c>GET /payments/instapay-instructions</c> has somewhere to be asserted. Configuration is
/// added <em>after</em> the base factory's, which is what lets it win.
/// </summary>
public sealed class InstaPayApiFactory : PurchaseApiFactory
{
    public const string PaymentUrl = "https://ipn.eg/S/beyondmovement/instapay/7Kq2";
    public const string QrImageUrl = "https://api.beyondmovement.test/brand/instapay-qr.png";
    public const string RecipientName = "Beyond Movement";
    public const string RecipientHandle = "beyondmovement@instapay";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:InstaPay:PaymentUrl"] = PaymentUrl,
                ["Payments:InstaPay:QrImageUrl"] = QrImageUrl,
                ["Payments:InstaPay:RecipientName"] = RecipientName,
                ["Payments:InstaPay:RecipientHandle"] = RecipientHandle,

                // Bound as an array, so the keys are indexed. Order is preserved, which the
                // contract promises: these are steps, not a bag of sentences.
                ["Payments:InstaPay:Instructions:0"] = "Open InstaPay and scan the QR code.",
                ["Payments:InstaPay:Instructions:1"] = "Send the exact amount shown on your purchase.",
                ["Payments:InstaPay:Instructions:2"] = "Your coach confirms it once the transfer arrives."
            }));
    }
}

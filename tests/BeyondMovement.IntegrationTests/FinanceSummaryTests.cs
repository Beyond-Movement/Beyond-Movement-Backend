using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// <c>GET /api/v1/finance/summary</c> — money in, money out, and the difference.
/// <para>
/// Every expected number below is arithmetic over <see cref="FinanceSummaryApiFactory"/>'s fixed
/// dataset, written down rather than computed, so a change in the windowing shows up as a wrong
/// total rather than as two pieces of the same bug agreeing with each other.
/// </para>
/// <para>
/// The dataset straddles each boundary deliberately: for every period there is a row on the first
/// day inside it and a row on the day before. A window that is off by one day fails.
/// </para>
/// </summary>
public sealed class FinanceSummaryTests(FinanceSummaryApiFactory factory)
    : IClassFixture<FinanceSummaryApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    private async Task<HttpClient> AdminClientAsync() =>
        await SignInAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private async Task<JsonElement> SummaryAsync(string? period = null)
    {
        var admin = await AdminClientAsync();
        var response = await admin.GetAsync(
            $"/api/v1/finance/summary{(period is null ? "" : "?period=" + period)}");

        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"summary {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static long Minor(JsonElement body, string field) => body.GetProperty(field).GetInt64();
    private static int Count(JsonElement body, string field) => body.GetProperty(field).GetInt32();

    // --- the totals, period by period ----------------------------------------

    /// <summary>
    /// Weekly. Income is the 400,000 payment plus the comped package, which adds one to the count
    /// and nothing to the total. Expenses are the two inside the Cairo week, one of them dated on
    /// the Monday the week begins.
    /// </summary>
    [Fact]
    public async Task Weekly_counts_only_what_falls_in_the_coachs_week()
    {
        var body = await SummaryAsync("Weekly");

        Assert.Equal("Weekly", body.GetProperty("period").GetString());

        Assert.Equal(400_000, Minor(body, "incomeMinor"));
        Assert.Equal(2, Count(body, "incomeCount"));

        Assert.Equal(530_000, Minor(body, "expensesMinor"));
        Assert.Equal(2, Count(body, "expenseCount"));

        // Spent more than was taken. Negative is a real answer, not something to clamp.
        Assert.Equal(-130_000, Minor(body, "netMinor"));
    }

    [Fact]
    public async Task Monthly_adds_what_happened_earlier_in_the_month()
    {
        var body = await SummaryAsync("Monthly");

        Assert.Equal(700_000, Minor(body, "incomeMinor"));
        Assert.Equal(3, Count(body, "incomeCount"));

        Assert.Equal(560_000, Minor(body, "expensesMinor"));
        Assert.Equal(4, Count(body, "expenseCount"));

        Assert.Equal(140_000, Minor(body, "netMinor"));
    }

    [Fact]
    public async Task Yearly_adds_what_happened_earlier_in_the_year()
    {
        var body = await SummaryAsync("Yearly");

        Assert.Equal(900_000, Minor(body, "incomeMinor"));
        Assert.Equal(4, Count(body, "incomeCount"));

        Assert.Equal(568_000, Minor(body, "expensesMinor"));
        Assert.Equal(6, Count(body, "expenseCount"));

        Assert.Equal(332_000, Minor(body, "netMinor"));
    }

    [Fact]
    public async Task AllTime_has_no_bound_at_all()
    {
        var body = await SummaryAsync("AllTime");

        Assert.Equal(1_000_000, Minor(body, "incomeMinor"));
        Assert.Equal(5, Count(body, "incomeCount"));

        Assert.Equal(570_000, Minor(body, "expensesMinor"));
        Assert.Equal(7, Count(body, "expenseCount"));

        Assert.Equal(430_000, Minor(body, "netMinor"));

        // Unbounded, so there is no window to report.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("fromUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("toUtc").ValueKind);
    }

    [Fact]
    public async Task The_period_defaults_to_monthly()
    {
        var explicitly = await SummaryAsync("Monthly");
        var byDefault = await SummaryAsync();

        Assert.Equal(explicitly.GetRawText(), byDefault.GetRawText());
    }

    // --- net -----------------------------------------------------------------

    [Theory]
    [InlineData("Weekly")]
    [InlineData("Monthly")]
    [InlineData("Yearly")]
    [InlineData("AllTime")]
    public async Task Net_is_always_income_minus_expenses(string period)
    {
        var body = await SummaryAsync(period);

        Assert.Equal(
            Minor(body, "incomeMinor") - Minor(body, "expensesMinor"),
            Minor(body, "netMinor"));
    }

    // --- pending is outstanding, never income --------------------------------

    /// <summary>
    /// The single most important rule in this response. A pending purchase is money that has not
    /// arrived, and a figure called income that counted it would be one nobody could reconcile
    /// against a bank balance.
    /// </summary>
    [Fact]
    public async Task A_pending_purchase_is_outstanding_and_never_income()
    {
        var weekly = await SummaryAsync("Weekly");

        // 500,000 is pending this week and is nowhere in the income figures.
        Assert.Equal(500_000, Minor(weekly, "pendingMinor"));
        Assert.Equal(1, Count(weekly, "pendingCount"));

        Assert.Equal(400_000, Minor(weekly, "incomeMinor"));
        Assert.Equal(2, Count(weekly, "incomeCount"));

        // Nor does it reach net, which is income minus expenses and nothing else.
        Assert.Equal(-130_000, Minor(weekly, "netMinor"));

        var allTime = await SummaryAsync("AllTime");

        Assert.Equal(750_000, Minor(allTime, "pendingMinor"));
        Assert.Equal(2, Count(allTime, "pendingCount"));
        Assert.Equal(1_000_000, Minor(allTime, "incomeMinor"));
    }

    /// <summary>
    /// Pending is dated by when the purchase was created, because a pending purchase has no
    /// payment date — that is the whole of what makes it pending. The older of the two falls
    /// outside this year and appears only in All Time.
    /// </summary>
    [Fact]
    public async Task Pending_is_bounded_by_when_the_purchase_was_asked_for()
    {
        Assert.Equal(500_000, Minor(await SummaryAsync("Monthly"), "pendingMinor"));
        Assert.Equal(500_000, Minor(await SummaryAsync("Yearly"), "pendingMinor"));
        Assert.Equal(750_000, Minor(await SummaryAsync("AllTime"), "pendingMinor"));
    }

    // --- what counts as income -----------------------------------------------

    /// <summary>
    /// A comped package is Paid and worth nothing. It is a real transaction, so it adds one to
    /// the count; it brought in no money, so it adds nothing to the total.
    /// </summary>
    [Fact]
    public async Task A_zero_price_paid_purchase_counts_once_and_contributes_nothing()
    {
        var body = await SummaryAsync("Weekly");

        // Two paid purchases this week, and only one of them was worth anything.
        Assert.Equal(2, Count(body, "incomeCount"));
        Assert.Equal(400_000, Minor(body, "incomeMinor"));
    }

    /// <summary>
    /// Every paid purchase in the fixture was recorded directly by the Admin — money taken in
    /// person. Excluding those would make this screen disagree with the coach's bank.
    /// </summary>
    [Fact]
    public async Task An_admin_recorded_sale_is_income_like_any_other()
    {
        var origins = await factory.QueryAsync(db => Task.FromResult(
            db.PackagePurchases.Select(x => x.Origin).Distinct().ToList()));

        Assert.Contains(BeyondMovement.Modules.Finance.Domain.PurchaseOrigin.AdminDirect, origins);

        // And they are all in the total, so nothing about Origin filters income.
        Assert.Equal(1_000_000, Minor(await SummaryAsync("AllTime"), "incomeMinor"));
    }

    // --- the window ----------------------------------------------------------

    /// <summary>
    /// Boundaries are Cairo midnights, not UTC midnights — which is 22:00 UTC the day before at
    /// this time of year. If the window were computed in UTC every one of these would be two
    /// hours out, and the expenses on the boundary days would fall on the wrong side.
    /// </summary>
    [Theory]
    [InlineData("Weekly", "2026-03-08T22:00:00Z", "2026-03-15T22:00:00Z")]
    [InlineData("Monthly", "2026-02-28T22:00:00Z", "2026-03-31T22:00:00Z")]
    [InlineData("Yearly", "2025-12-31T22:00:00Z", "2026-12-31T22:00:00Z")]
    public async Task The_window_is_the_coachs_local_calendar_converted_to_utc(
        string period, string from, string to)
    {
        var body = await SummaryAsync(period);

        Assert.Equal(
            DateTime.Parse(from).ToUniversalTime(),
            body.GetProperty("fromUtc").GetDateTime().ToUniversalTime());

        Assert.Equal(
            DateTime.Parse(to).ToUniversalTime(),
            body.GetProperty("toUtc").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task The_zone_the_boundaries_were_computed_in_is_reported_back()
    {
        var body = await SummaryAsync("Monthly");

        Assert.Equal(FinanceSummaryApiFactory.CoachTimeZone, body.GetProperty("timeZone").GetString());
    }

    // --- shape, currency and isolation ---------------------------------------

    [Fact]
    public async Task The_response_carries_the_fields_the_contract_promises()
    {
        var body = await SummaryAsync("Monthly");

        Assert.Equal(
            ["period", "timeZone", "fromUtc", "toUtc", "currency",
             "incomeMinor", "expensesMinor", "netMinor",
             "incomeCount", "expenseCount", "pendingMinor", "pendingCount"],
            body.EnumerateObject().Select(p => p.Name).ToArray());

        // One currency for the whole response rather than one per figure: the platform bills in
        // EGP only, and every amount is an integer count of piastres.
        Assert.Equal("EGP", body.GetProperty("currency").GetString());
    }

    /// <summary>
    /// Another coach's nine million in income and eight million in expenses sit in the same
    /// tables, dated inside every window. Neither appears in any figure above — which is what
    /// scoping by the coach id on the token buys.
    /// </summary>
    [Fact]
    public async Task Another_coachs_money_never_reaches_these_totals()
    {
        var body = await SummaryAsync("AllTime");

        Assert.Equal(1_000_000, Minor(body, "incomeMinor"));
        Assert.Equal(570_000, Minor(body, "expensesMinor"));

        // Both are demonstrably in the database; they are simply not this coach's.
        Assert.Equal(1, await factory.QueryAsync(db => Task.FromResult(
            db.PackagePurchases.Count(x => x.PriceMinor == 9_000_000))));
        Assert.Equal(1, await factory.QueryAsync(db => Task.FromResult(
            db.Expenses.Count(x => x.AmountMinor == 8_000_000))));
    }

    [Fact]
    public async Task The_summary_is_admin_only()
    {
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/finance/summary")).StatusCode);

        var athlete = await SignInAsync("finance-payer@nowhere.test", AthleteApiFactory.AthletePassword);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.GetAsync("/api/v1/finance/summary")).StatusCode);
    }

    /// <summary>
    /// Admin Home is delivery statistics and carries no money; this is money and carries no
    /// sessions. The two share only their period machinery, and this phase does not put financial
    /// cards on Home.
    /// </summary>
    [Fact]
    public async Task The_dashboard_still_carries_no_money()
    {
        var admin = await AdminClientAsync();

        var dashboard = await admin.GetFromJsonAsync<JsonElement>("/api/v1/dashboard/admin");
        var statistics = dashboard.GetProperty("statistics");

        foreach (var absent in new[]
                 { "incomeMinor", "expensesMinor", "netMinor", "pendingMinor", "currency" })
        {
            Assert.False(statistics.TryGetProperty(absent, out _),
                $"{absent} must not be on the dashboard in this phase");
        }
    }
}

/// <summary>
/// Its own fixture, seeded with nothing at all, because an empty answer cannot be asserted in a
/// database that other tests have put money in.
/// </summary>
public sealed class EmptyFinanceApiFactory : ApiFactory;

/// <summary>A coach who has taken nothing and spent nothing still gets a screen.</summary>
public sealed class EmptyFinanceSummaryTests(EmptyFinanceApiFactory factory)
    : IClassFixture<EmptyFinanceApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    [Theory]
    [InlineData("Weekly")]
    [InlineData("Monthly")]
    [InlineData("Yearly")]
    [InlineData("AllTime")]
    public async Task A_period_with_nothing_in_it_returns_zeros_rather_than_a_404(string period)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword });

        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var response = await client.GetAsync($"/api/v1/finance/summary?period={period}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Zero is the answer, not an absence. The screen shows zeros.
        Assert.Equal(0, body.GetProperty("incomeMinor").GetInt64());
        Assert.Equal(0, body.GetProperty("expensesMinor").GetInt64());
        Assert.Equal(0, body.GetProperty("netMinor").GetInt64());
        Assert.Equal(0, body.GetProperty("incomeCount").GetInt32());
        Assert.Equal(0, body.GetProperty("expenseCount").GetInt32());
        Assert.Equal(0, body.GetProperty("pendingMinor").GetInt64());
        Assert.Equal(0, body.GetProperty("pendingCount").GetInt32());

        Assert.Equal("EGP", body.GetProperty("currency").GetString());
    }
}

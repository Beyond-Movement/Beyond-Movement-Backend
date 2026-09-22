using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// Its own fixture: these tests create, edit and delete expenses and count what comes back, so
/// nothing else may be adding them.
/// </summary>
public sealed class ExpenseApiFactory : PurchaseApiFactory;

/// <summary>
/// The coach's own costs — <c>/api/v1/expenses</c>.
/// <para>
/// Deliberately the simplest financial record the product has: a title, an amount and a date.
/// There is no category, no receipt and no athlete, and the tests below assert the absence of
/// the last of those as carefully as they assert the presence of the rest — an expense that
/// could name an athlete would be the start of the bookkeeping this is not.
/// </para>
/// </summary>
public sealed class ExpenseTests(ExpenseApiFactory factory) : IClassFixture<ExpenseApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    private async Task<HttpClient> AdminClientAsync() =>
        await SignInAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    private async Task<HttpClient> AthleteClientAsync(string email) =>
        await SignInAsync(email, AthleteApiFactory.AthletePassword);

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task<JsonElement> CreateAsync(
        HttpClient admin, string title, long amountMinor, DateOnly incurredOn, string? note = null)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/expenses", new
        {
            title,
            amountMinor,
            incurredOn = incurredOn.ToString("yyyy-MM-dd"),
            note
        });

        if (response.StatusCode != HttpStatusCode.Created)
            Assert.Fail($"create {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ListAsync(HttpClient admin, string query = "")
    {
        var response = await admin.GetAsync(
            $"/api/v1/expenses{(query.Length == 0 ? "" : "?" + query)}");

        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"list {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Guid[] IdsOf(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid())];

    /// <summary>A date nothing else in this suite uses, so a test can count its own rows.</summary>
    private static DateOnly On(int year, int month, int day) => new(year, month, day);

    // --- create --------------------------------------------------------------

    [Fact]
    public async Task Recording_an_expense_returns_it_with_the_server_controlled_fields_filled_in()
    {
        var admin = await AdminClientAsync();

        var expense = await CreateAsync(
            admin, "Office rental", 50_000, On(2026, 4, 1), "April, paid in cash");

        Assert.Equal("Office rental", expense.GetProperty("title").GetString());
        Assert.Equal(50_000, expense.GetProperty("amountMinor").GetInt64());
        Assert.Equal("2026-04-01", expense.GetProperty("incurredOn").GetString());
        Assert.Equal("April, paid in cash", expense.GetProperty("note").GetString());

        // Never sent by the client, and the only currency this platform bills in.
        Assert.Equal("EGP", expense.GetProperty("currency").GetString());

        Assert.NotEqual(Guid.Empty, expense.GetProperty("id").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, expense.GetProperty("createdAtUtc").ValueKind);
    }

    /// <summary>
    /// The shape is stated exactly, because a field added here is a field the coach has to fill
    /// in - and because an expense must never grow an athlete.
    /// </summary>
    [Fact]
    public async Task An_expense_carries_the_fields_the_contract_promises_and_no_others()
    {
        var admin = await AdminClientAsync();
        var expense = await CreateAsync(admin, "Shape", 1_000, On(2026, 4, 2));

        Assert.Equal(
            ["id", "title", "amountMinor", "currency", "incurredOn", "note",
             "createdAtUtc", "updatedAtUtc"],
            expense.EnumerateObject().Select(p => p.Name).ToArray());

        // No category in this phase, and no athlete ever.
        foreach (var absent in new[] { "category", "athleteUserId", "athleteProfileId", "coachId", "receiptFileId" })
            Assert.False(expense.TryGetProperty(absent, out _), $"{absent} must not be present");
    }

    [Fact]
    public async Task A_note_is_optional_and_a_blank_one_reads_back_as_null()
    {
        var admin = await AdminClientAsync();

        var omitted = await CreateAsync(admin, "No note", 1_000, On(2026, 4, 3));
        Assert.Equal(JsonValueKind.Null, omitted.GetProperty("note").ValueKind);

        var blank = await CreateAsync(admin, "Blank note", 1_000, On(2026, 4, 3), "   ");
        Assert.Equal(JsonValueKind.Null, blank.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task A_title_is_trimmed()
    {
        var admin = await AdminClientAsync();
        var expense = await CreateAsync(admin, "  Padded  ", 1_000, On(2026, 4, 4));

        Assert.Equal("Padded", expense.GetProperty("title").GetString());
    }

    // --- validation ----------------------------------------------------------

    [Theory]
    [InlineData("", 1000, "title")]
    [InlineData("   ", 1000, "title")]
    [InlineData("ok", 0, "amountMinor")]
    [InlineData("ok", -1, "amountMinor")]
    [InlineData("ok", 1_000_000_001, "amountMinor")]
    public async Task An_invalid_expense_is_rejected_and_names_the_field(
        string title, long amountMinor, string field)
    {
        var admin = await AdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/v1/expenses", new
        {
            title,
            amountMinor,
            incurredOn = "2026-04-05"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", problem.GetProperty("errorCode").GetString());
        Assert.Contains(field, problem.GetProperty("errors").GetRawText(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Zero is the interesting one. A package may legitimately cost nothing - a comped athlete -
    /// but a zero-value expense is a typo, and one that got through would quietly distort every
    /// summary it appeared in. The two rules differ on purpose.
    /// </summary>
    [Fact]
    public async Task One_piastre_is_allowed_where_zero_is_not()
    {
        var admin = await AdminClientAsync();

        var expense = await CreateAsync(admin, "One piastre", 1, On(2026, 4, 6));
        Assert.Equal(1, expense.GetProperty("amountMinor").GetInt64());
    }

    [Fact]
    public async Task A_title_or_note_beyond_its_limit_is_rejected()
    {
        var admin = await AdminClientAsync();

        var longTitle = await admin.PostAsJsonAsync("/api/v1/expenses", new
        {
            title = new string('t', 201),
            amountMinor = 1_000,
            incurredOn = "2026-04-07"
        });

        Assert.Equal(HttpStatusCode.BadRequest, longTitle.StatusCode);

        var longNote = await admin.PostAsJsonAsync("/api/v1/expenses", new
        {
            title = "Fine",
            amountMinor = 1_000,
            incurredOn = "2026-04-07",
            note = new string('n', 1001)
        });

        Assert.Equal(HttpStatusCode.BadRequest, longNote.StatusCode);

        // And the boundary itself is accepted, so the limit is the documented one.
        var atLimit = await CreateAsync(
            admin, new string('t', 200), 1_000, On(2026, 4, 7), new string('n', 1000));

        Assert.Equal(200, atLimit.GetProperty("title").GetString()!.Length);
    }

    /// <summary>
    /// Neither a past nor a future date is refused: entering last year's receipts and recording a
    /// cost already committed for next month are both things a coach legitimately does.
    /// </summary>
    [Fact]
    public async Task A_date_far_in_the_past_or_the_future_is_accepted()
    {
        var admin = await AdminClientAsync();

        await CreateAsync(admin, "Old receipt", 1_000, On(2020, 1, 1));
        await CreateAsync(admin, "Committed cost", 1_000, On(2030, 12, 31));
    }

    // --- read and update -----------------------------------------------------

    [Fact]
    public async Task An_expense_can_be_rewritten_whole()
    {
        var admin = await AdminClientAsync();
        var created = await CreateAsync(admin, "Before", 1_000, On(2026, 5, 1), "old note");
        var id = created.GetProperty("id").GetGuid();

        var response = await admin.PutAsJsonAsync($"/api/v1/expenses/{id}", new
        {
            title = "After",
            amountMinor = 2_500,
            incurredOn = "2026-05-09"
        });

        response.EnsureSuccessStatusCode();
        var edited = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, edited.GetProperty("id").GetGuid());
        Assert.Equal("After", edited.GetProperty("title").GetString());
        Assert.Equal(2_500, edited.GetProperty("amountMinor").GetInt64());
        Assert.Equal("2026-05-09", edited.GetProperty("incurredOn").GetString());

        // A full replacement, not a patch: the note was left out of the body, so it is cleared.
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task Editing_applies_the_same_validation_as_creating()
    {
        var admin = await AdminClientAsync();
        var id = (await CreateAsync(admin, "Valid", 1_000, On(2026, 5, 2))).GetProperty("id").GetGuid();

        var response = await admin.PutAsJsonAsync($"/api/v1/expenses/{id}", new
        {
            title = "Still valid",
            amountMinor = 0,
            incurredOn = "2026-05-02"
        });

        // An expense cannot be edited into a state it could not have been created in.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Editing_an_unknown_expense_is_404()
    {
        var admin = await AdminClientAsync();

        var response = await admin.PutAsJsonAsync($"/api/v1/expenses/{Guid.NewGuid()}", new
        {
            title = "Nothing",
            amountMinor = 1_000,
            incurredOn = "2026-05-03"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("EXPENSE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    // --- delete --------------------------------------------------------------

    [Fact]
    public async Task Deleting_an_expense_removes_it_for_good()
    {
        var admin = await AdminClientAsync();
        var id = (await CreateAsync(admin, "Temporary", 9_999, On(2026, 6, 1))).GetProperty("id").GetGuid();

        var deleted = await admin.DeleteAsync($"/api/v1/expenses/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // A hard delete: the row is gone, so a repeat is a 404 rather than a silent success.
        var again = await admin.DeleteAsync($"/api/v1/expenses/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        Assert.DoesNotContain(id, IdsOf(await ListAsync(admin, "from=2026-06-01&to=2026-06-01")));

        Assert.Equal(0, await factory.QueryAsync(db =>
            db.Expenses.CountAsync(x => x.Id == id)));
    }

    [Fact]
    public async Task Deleting_an_unknown_expense_is_404()
    {
        var admin = await AdminClientAsync();

        var response = await admin.DeleteAsync($"/api/v1/expenses/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("EXPENSE_NOT_FOUND",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
    }

    // --- coach isolation -----------------------------------------------------

    /// <summary>
    /// The coach id comes from the token and never from the route or body, so moving the row to
    /// another coach is enough to make it unreachable - no second Admin account is needed to
    /// prove it.
    /// </summary>
    [Fact]
    public async Task An_expense_belonging_to_another_coach_is_invisible_and_404_on_every_verb()
    {
        var admin = await AdminClientAsync();
        var id = (await CreateAsync(admin, "Someone else's", 7_777, On(2026, 7, 1))).GetProperty("id").GetGuid();

        await factory.QueryAsync(async db => await db.Database.ExecuteSqlAsync(
            $"""update "Expenses" set "CoachId" = {Guid.NewGuid()} where "Id" = {id}"""));

        Assert.DoesNotContain(id, IdsOf(await ListAsync(admin, "from=2026-07-01&to=2026-07-01")));

        var edit = await admin.PutAsJsonAsync($"/api/v1/expenses/{id}", new
        {
            title = "Hijack",
            amountMinor = 1,
            incurredOn = "2026-07-01"
        });

        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/v1/expenses/{id}")).StatusCode);

        // It still exists - it simply is not this coach's to see or touch.
        Assert.Equal(1, await factory.QueryAsync(db => db.Expenses.CountAsync(x => x.Id == id)));
    }

    [Fact]
    public async Task An_expense_never_changes_hands_through_the_request_body()
    {
        var admin = await AdminClientAsync();
        var stranger = Guid.NewGuid();

        var response = await admin.PostAsJsonAsync("/api/v1/expenses", new
        {
            title = "Coach in the body",
            amountMinor = 1_000,
            incurredOn = "2026-07-02",
            coachId = stranger
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The extra field is ignored, not honoured: the expense belongs to the signed-in Admin.
        Assert.Equal(0, await factory.QueryAsync(db =>
            db.Expenses.CountAsync(x => x.Id == id && x.CoachId == stranger)));
    }

    // --- authorization -------------------------------------------------------

    [Fact]
    public async Task Expenses_are_admin_only_and_an_athlete_can_reach_none_of_them()
    {
        var admin = await AdminClientAsync();
        var (_, email) = await factory.NewAthleteAsync();
        var id = (await CreateAsync(admin, "Admin only", 1_000, On(2026, 7, 3))).GetProperty("id").GetGuid();

        var athlete = await AthleteClientAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, (await athlete.GetAsync("/api/v1/expenses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.PostAsJsonAsync("/api/v1/expenses",
                new { title = "x", amountMinor = 1, incurredOn = "2026-07-03" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await athlete.DeleteAsync($"/api/v1/expenses/{id}")).StatusCode);

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/expenses")).StatusCode);
    }

    // --- listing, paging and date filtering ----------------------------------

    [Fact]
    public async Task The_list_comes_back_in_the_standard_paged_envelope()
    {
        var admin = await AdminClientAsync();

        var body = await ListAsync(admin, "from=2099-01-01&to=2099-01-02");

        Assert.Equal(
            ["items", "page", "pageSize", "totalCount", "totalPages", "hasNextPage", "hasPreviousPage"],
            body.EnumerateObject().Select(p => p.Name).ToArray());

        // A range with nothing in it is an empty page, not a 404.
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
    }

    /// <summary>
    /// from and to are BOTH inclusive, unlike the half-open UTC windows the session endpoints
    /// use. These are dates a person typed: 1-31 March means the 31st included, and an exclusive
    /// bound there silently drops one day's costs.
    /// </summary>
    [Fact]
    public async Task The_date_filter_includes_both_ends()
    {
        var admin = await AdminClientAsync();

        var before = (await CreateAsync(admin, "Day before", 1_000, On(2026, 8, 9))).GetProperty("id").GetGuid();
        var first = (await CreateAsync(admin, "First day", 1_000, On(2026, 8, 10))).GetProperty("id").GetGuid();
        var middle = (await CreateAsync(admin, "Middle", 1_000, On(2026, 8, 15))).GetProperty("id").GetGuid();
        var last = (await CreateAsync(admin, "Last day", 1_000, On(2026, 8, 20))).GetProperty("id").GetGuid();
        var after = (await CreateAsync(admin, "Day after", 1_000, On(2026, 8, 21))).GetProperty("id").GetGuid();

        var ids = IdsOf(await ListAsync(admin, "from=2026-08-10&to=2026-08-20"));

        Assert.Contains(first, ids);
        Assert.Contains(middle, ids);
        Assert.Contains(last, ids);
        Assert.DoesNotContain(before, ids);
        Assert.DoesNotContain(after, ids);
    }

    [Fact]
    public async Task Either_bound_may_be_sent_alone()
    {
        var admin = await AdminClientAsync();

        var early = (await CreateAsync(admin, "Early", 1_000, On(2026, 9, 1))).GetProperty("id").GetGuid();
        var late = (await CreateAsync(admin, "Late", 1_000, On(2026, 9, 30))).GetProperty("id").GetGuid();

        var fromOnly = IdsOf(await ListAsync(admin, "from=2026-09-15&to=2026-09-30"));
        Assert.Contains(late, fromOnly);
        Assert.DoesNotContain(early, fromOnly);

        var toOnly = IdsOf(await ListAsync(admin, "from=2026-09-01&to=2026-09-14"));
        Assert.Contains(early, toOnly);
        Assert.DoesNotContain(late, toOnly);
    }

    [Fact]
    public async Task Paging_walks_the_list_newest_first_without_repeating_or_skipping()
    {
        var admin = await AdminClientAsync();

        // Five consecutive days in a month this suite uses nowhere else.
        var expected = new List<Guid>();
        for (var day = 1; day <= 5; day++)
            expected.Add((await CreateAsync(admin, $"Paged {day}", 1_000, On(2026, 10, day)))
                .GetProperty("id").GetGuid());

        var seen = new List<Guid>();
        var page = 1;

        while (true)
        {
            var body = await ListAsync(admin, $"from=2026-10-01&to=2026-10-05&page={page}&pageSize=2");

            Assert.Equal(5, body.GetProperty("totalCount").GetInt32());
            Assert.Equal(3, body.GetProperty("totalPages").GetInt32());

            seen.AddRange(IdsOf(body));

            if (!body.GetProperty("hasNextPage").GetBoolean()) break;
            page++;
        }

        Assert.Equal(3, page);

        // Newest first, so the walk is the reverse of the order they were incurred in.
        Assert.Equal([.. Enumerable.Reverse(expected)], seen);
    }

    [Theory]
    [InlineData("page=0", 1, 20)]
    [InlineData("page=-3", 1, 20)]
    [InlineData("pageSize=0", 1, 1)]
    [InlineData("pageSize=9999", 1, 100)]
    public async Task Paging_outside_the_range_is_clamped_rather_than_rejected(
        string query, int expectedPage, int expectedPageSize)
    {
        var admin = await AdminClientAsync();

        var body = await ListAsync(admin, query);

        Assert.Equal(expectedPage, body.GetProperty("page").GetInt32());
        Assert.Equal(expectedPageSize, body.GetProperty("pageSize").GetInt32());
    }

    // --- audit ---------------------------------------------------------------

    /// <summary>
    /// Money leaving the practice is exactly what the audit log exists for. The delete entry
    /// matters most: the row is gone for good, so the log is the only remaining evidence of what
    /// was removed, which is why it carries the amount and the date.
    /// </summary>
    [Fact]
    public async Task Creating_editing_and_deleting_are_each_audited()
    {
        var admin = await AdminClientAsync();
        var id = (await CreateAsync(admin, "Audited", 4_242, On(2026, 11, 11))).GetProperty("id").GetGuid();

        (await admin.PutAsJsonAsync($"/api/v1/expenses/{id}", new
        {
            title = "Audited, edited",
            amountMinor = 5_555,
            incurredOn = "2026-11-12"
        })).EnsureSuccessStatusCode();

        (await admin.DeleteAsync($"/api/v1/expenses/{id}")).EnsureSuccessStatusCode();

        var entries = await factory.QueryAsync(db => db.AuditLogs.AsNoTracking()
            .Where(x => x.Details!.Contains(id.ToString()))
            .OrderBy(x => x.OccurredAtUtc)
            .ToListAsync());

        Assert.Equal(
            ["ExpenseCreated", "ExpenseEdited", "ExpenseDeleted"],
            entries.Select(x => x.Action).ToArray());

        // Every entry names the Admin who did it.
        Assert.All(entries, x => Assert.NotNull(x.ActorUserId));

        // The delete records what was destroyed, so the log outlives the row.
        var deleted = entries.Single(x => x.Action == "ExpenseDeleted");
        Assert.Contains("amountMinor=5555", deleted.Details);
        Assert.Contains("2026-11-12", deleted.Details);
    }
}

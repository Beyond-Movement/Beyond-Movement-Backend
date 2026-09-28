using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeyondMovement.Infrastructure;
using BeyondMovement.Infrastructure.Storage;
using BeyondMovement.Modules.Scheduling.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BeyondMovement.IntegrationTests;

/// <summary>
/// The app with the REAL <see cref="S3ObjectStorage"/> in place of the fake, and with the
/// configuration a fresh clone or a local machine has: no bucket, no region, no AWS credentials.
/// </summary>
public sealed class RealStorageApiFactory : ApiFactory
{
    public const string Athlete = "storage-config@nowhere.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        });
    }

    protected override async Task InitializeCoreAsync()
    {
        await base.InitializeCoreAsync();

        using var scope = Services.CreateScope();
        await AthleteApiFactory.AddAthleteAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider, Athlete, "Storage Config", "Tennis", new DateOnly(2000, 1, 1));
    }
}

/// <summary>
/// The bucket is a deployment choice: <c>Storage__S3__BucketName</c> and <c>Storage__S3__Region</c>
/// are supplied per environment and ship empty. These pin what that means at run time.
/// </summary>
public sealed class StorageConfigurationTests(RealStorageApiFactory factory) : IClassFixture<RealStorageApiFactory>
{
    private sealed record AuthPayload(string AccessToken, string RefreshToken);

    [Fact]
    public void The_shipped_configuration_names_no_bucket()
    {
        var options = factory.Services.GetRequiredService<IOptions<StorageOptions>>().Value;

        Assert.False(options.IsConfigured);
        Assert.Equal("", options.S3.BucketName);
        Assert.Equal("", options.S3.Region);
        Assert.IsType<S3ObjectStorage>(factory.Services.GetRequiredService<IObjectStorage>());
    }

    /// <summary>
    /// A machine with no bucket and no AWS credentials starts, serves notes, and answers only the
    /// storage-backed call with 503 STORAGE_UNAVAILABLE — creating nothing.
    /// </summary>
    [Fact]
    public async Task Without_a_bucket_the_app_runs_and_attachments_are_unavailable()
    {
        var admin = await AdminClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/health")).StatusCode);

        var sessionId = await SeedObservationAsync();
        var note = await admin.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/notes", new { title = "T", content = "C" });
        Assert.Equal(HttpStatusCode.Created, note.StatusCode);
        var noteId = (await note.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // A note with no images reads normally: nothing needs signing.
        var list = await admin.GetAsync($"/api/v1/sessions/{sessionId}/notes");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var upload = await admin.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/notes/{noteId}/attachments", new { contentType = "image/jpeg", sizeBytes = 100 });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, upload.StatusCode);
        Assert.Equal("STORAGE_UNAVAILABLE",
            (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());

        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SessionNoteAttachments.AnyAsync(x => x.SessionNoteId == noteId));
    }

    /// <summary>
    /// What ECS supplies. It must start without contacting AWS — credentials are only looked up
    /// when an attachment operation actually runs.
    /// </summary>
    [Fact]
    public async Task A_bucket_and_region_from_configuration_are_used_and_startup_does_not_contact_aws()
    {
        using var configured = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:S3:BucketName"] = "beyond-movement-files-745059801486-ap-south-1-an",
                ["Storage:S3:Region"] = "ap-south-1"
            })));

        var options = configured.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
        Assert.True(options.IsConfigured);
        Assert.Equal("beyond-movement-files-745059801486-ap-south-1-an", options.S3.BucketName);
        Assert.Equal("ap-south-1", options.S3.Region);

        Assert.Equal(HttpStatusCode.OK, (await configured.CreateClient().GetAsync("/health")).StatusCode);
    }

    /// <summary>A name without a region is a deployment mistake, caught at startup rather than on the first upload.</summary>
    [Fact]
    public void A_bucket_without_a_region_fails_at_startup()
    {
        using var halfConfigured = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:S3:BucketName"] = "some-bucket"
            })));

        var error = Record.Exception(() => halfConfigured.CreateClient());

        Assert.NotNull(error);
        Assert.Contains("Storage:S3:Region", Flatten(error));
    }

    [Fact]
    public void A_malformed_service_url_fails_at_startup()
    {
        using var bad = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:S3:BucketName"] = "local",
                ["Storage:S3:ServiceUrl"] = "not a url"
            })));

        var error = Record.Exception(() => bad.CreateClient());

        Assert.NotNull(error);
        Assert.Contains("Storage:S3:ServiceUrl", Flatten(error));
    }

    private static string Flatten(Exception e)
    {
        var messages = new List<string>();
        for (Exception? x = e; x is not null; x = x.InnerException) messages.Add(x.Message);
        if (e is AggregateException agg) messages.AddRange(agg.Flatten().InnerExceptions.Select(x => x.Message));
        return string.Join(" | ", messages);
    }

    private async Task<Guid> SeedObservationAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var profile = await (from user in db.Users
                             join p in db.AthleteProfiles on user.Id equals p.UserId
                             where user.Email == RealStorageApiFactory.Athlete
                             select p).SingleAsync();

        var start = DateTime.UtcNow.AddDays(-1);
        var session = Session.CreateObservation(
            profile.CoachId, profile.Id, start, start.AddMinutes(60), "Pool", deductsSession: false, DateTime.UtcNow);

        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = ApiFactory.AdminEmail, password = ApiFactory.AdminPassword });

        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }
}

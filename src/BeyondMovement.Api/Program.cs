using BeyondMovement.Api.Attendance;
using BeyondMovement.Api.Authentication;
using BeyondMovement.Api.Endpoints;
using BeyondMovement.Api;
using BeyondMovement.Api.Athletes;
using BeyondMovement.Api.Packages;
using BeyondMovement.Modules.Athletes.Features;
using BeyondMovement.Modules.Packages.Features;
using BeyondMovement.Modules.Packages.Persistence;
using BeyondMovement.Api.Middleware;
using BeyondMovement.Modules.Identity.Features.AccountStatus;
using BeyondMovement.Api.OpenApi;
using BeyondMovement.Modules.Athletes.Persistence;
using BeyondMovement.Modules.Identity.Features.Invitations;
using BeyondMovement.Modules.Identity.Features.Register;
using BeyondMovement.Api.Seeding;
using BeyondMovement.Infrastructure;
using BeyondMovement.Infrastructure.Auditing;
using BeyondMovement.Infrastructure.Email;
using BeyondMovement.Infrastructure.Google;
using BeyondMovement.Infrastructure.Calendly;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Features.ChangePassword;
using BeyondMovement.Modules.Identity.Features.CurrentUser;
using BeyondMovement.Modules.Identity.Features.ForgotPassword;
using BeyondMovement.Modules.Identity.Features.GoogleSignIn;
using BeyondMovement.Modules.Identity.Features.Login;
using BeyondMovement.Modules.Identity.Features.Logout;
using BeyondMovement.Api.Dashboard;
using BeyondMovement.Api.Finance;
using BeyondMovement.Modules.Finance.Features;
using BeyondMovement.Modules.Finance.Payments;
using BeyondMovement.Modules.Finance.Persistence;
using BeyondMovement.Modules.Identity.Features.Refresh;
using BeyondMovement.Modules.Identity.Features.ResetPassword;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.Modules.Scheduling.Calendly;
using BeyondMovement.Modules.Scheduling.Features;
using BeyondMovement.Modules.Scheduling.Persistence;
using BeyondMovement.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;
using Hangfire;
using Hangfire.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// --- logging -------------------------------------------------------------
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
          .Enrich.FromLogContext()
          .WriteTo.Console());

// --- database ------------------------------------------------------------
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Modules depend on their own DbContext abstraction, never on AppDbContext directly.
builder.Services.AddScoped<IIdentityDbContext>(sp => sp.GetRequiredService<AppDbContext>());

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();

// Enums travel as their names ("Admin", "Active"), never as integers, so the contract
// advertises exact values the client can switch on.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// --- identity module -----------------------------------------------------
// Validated at startup, not on first use. Without this the app boots happily and then
// returns 500 for every request - including anonymous ones - because the authentication
// handler cannot build its options. Failing here says exactly what is missing.
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.SigningKey),
        "Jwt:SigningKey is not configured. Set it with: dotnet user-secrets set " +
        "\"Jwt:SigningKey\" \"<64+ character value>\" --project src/BeyondMovement.Api")
    .Validate(o => string.IsNullOrWhiteSpace(o.SigningKey) || Encoding.UTF8.GetByteCount(o.SigningKey) >= 32,
        "Jwt:SigningKey must be at least 32 bytes, or HMAC-SHA256 signing fails with IDX10720.")
    .ValidateOnStart();
builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection(GoogleOptions.SectionName));

// Only the vetted password-hashing part of ASP.NET Core Identity is used, not the full stack.
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IGoogleTokenValidator, GoogleTokenValidator>();

// --- email ---------------------------------------------------------------
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.Configure<EmailBrandingOptions>(
    builder.Configuration.GetSection(EmailBrandingOptions.SectionName));

var emailOptions = builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new();

// Postmark first, then a local SMTP catcher, then the console. Each step down is a step
// further from a real inbox, and the app says at startup which one it landed on.
if (emailOptions.PostmarkConfigured)
{
    // Typed client: pooled connections and a sane timeout, rather than a new socket per email.
    builder.Services.AddHttpClient<IEmailSender, PostmarkEmailSender>(client =>
        client.Timeout = TimeSpan.FromSeconds(15));

    Console.WriteLine("Email: sending through Postmark.");
}
else if (emailOptions.SmtpConfigured)
{
    builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

    // Say plainly whether this reaches real people. Mail catchers run on the local machine;
    // anything else is a real server and a real inbox.
    var isLocalCatcher = emailOptions.Smtp.Host is "localhost" or "127.0.0.1" or "mailpit";

    Console.WriteLine(isLocalCatcher
        ? $"Email: sending to the local mail catcher at {emailOptions.Smtp.Host}:{emailOptions.Smtp.Port}. " +
          "Nothing leaves this machine - read it at http://localhost:8025"
        : $"Email: sending REAL mail via {emailOptions.Smtp.Host}:{emailOptions.Smtp.Port} " +
          $"as {emailOptions.FromAddress}. Messages will reach real inboxes.");
}
else
{
    // A fresh clone has no email account, so fall back rather than fail. Codes are then read
    // from the API console - the README says so, and this warning repeats it at run time.
    builder.Services.AddScoped<IEmailSender, ConsoleEmailSender>();

    Console.WriteLine(
        "WARNING  No email transport configured. Invitation codes and password-reset links " +
        "will be printed to this console instead of sent. Run 'docker compose up -d mailpit' " +
        "to receive them in a real inbox at http://localhost:8025.");
}

builder.Services.AddScoped<LoginHandler>();
builder.Services.AddScoped<GoogleSignInHandler>();
builder.Services.AddScoped<RefreshHandler>();
builder.Services.AddScoped<LogoutHandler>();
builder.Services.AddScoped<ForgotPasswordHandler>();
builder.Services.AddScoped<ResetPasswordHandler>();
builder.Services.AddScoped<ChangePasswordHandler>();
builder.Services.AddScoped<CurrentUserHandler>();
builder.Services.AddScoped<CreateInvitationHandler>();
builder.Services.AddScoped<ValidateInvitationHandler>();
builder.Services.AddScoped<ManageInvitationsHandler>();
builder.Services.AddScoped<RegisterHandler>();
builder.Services.AddSingleton<IRegistrationTokenService, RegistrationTokenService>();

// --- athletes module -----------------------------------------------------
builder.Services.AddScoped<IAthletesDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<CreateProfileHandler>();
builder.Services.AddScoped<CompleteProfileHandler>();
builder.Services.AddScoped<SetAccountStatusHandler>();
builder.Services.AddScoped<AthleteDirectory>();

// --- packages module ---------------------------------------------------
builder.Services.AddScoped<IPackagesDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<PackageOptionHandler>();
builder.Services.AddScoped<CustomPriceHandler>();
builder.Services.AddScoped<SetLoyaltyHandler>();

// Spans Packages and Athletes, so it lives in the composition root and reads only.
builder.Services.AddScoped<CatalogueReader>();
builder.Services.AddScoped<PackagePurchaseService>();

// Purchase and payment - Phase 8. Writes to Finance and Packages at once, so it lives in the
// Api project for the same reason attendance does.
builder.Services.Configure<InstaPayOptions>(
    builder.Configuration.GetSection(InstaPayOptions.SectionName));
builder.Services.AddScoped<IFinanceDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<PurchaseCheckoutService>();

// Admin Home - Phase 9. Reads across Scheduling, Athletes and Identity, so it is a read model
// in the composition root like AthleteDirectory and CatalogueReader.
builder.Services.AddScoped<AdminDashboardReader>();

// Attendance - Phase 6. Writes to Scheduling and Packages at once, so it lives in the Api
// project rather than in either module (CLAUDE.md section 4).
builder.Services.Configure<FeatureOptions>(builder.Configuration.GetSection(FeatureOptions.SectionName));
builder.Services.AddScoped<AttendanceService>();

// --- scheduling / Calendly ----------------------------------------------
builder.Services.AddOptions<CalendlyOptions>()
    .Bind(builder.Configuration.GetSection(CalendlyOptions.SectionName));
builder.Services.AddScoped<ISchedulingDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<SchedulingService>();
builder.Services.AddSingleton<ICalendlyWebhookVerifier, CalendlyWebhookVerifier>();
builder.Services.AddSingleton<ICalendlyWebhookParser, CalendlyWebhookParser>();
builder.Services.AddHttpClient<ICalendlyClient, CalendlyClient>((sp, client) =>
{
    var configured = sp.GetRequiredService<IOptions<CalendlyOptions>>().Value;
    client.BaseAddress = new Uri(configured.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});
var jobsEnabled = builder.Configuration.GetValue("Jobs:Enabled", true);
if (jobsEnabled)
{
    builder.Services.AddHangfire(configuration => configuration
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(
            builder.Configuration.GetConnectionString("Postgres"))));
    builder.Services.AddHangfireServer(options => options.Queues = ["default", "calendly"]);
    builder.Services.AddSingleton<ISchedulingJobScheduler, HangfireSchedulingJobScheduler>();
}
else builder.Services.AddSingleton<ISchedulingJobScheduler, DisabledSchedulingJobScheduler>();
builder.Services.AddScoped<CalendlySynchronizationJobs>();

builder.Services.AddValidatorsFromAssemblyContaining<LoginValidator>();
builder.Services.AddValidatorsFromAssemblyContaining<SavePackageOptionValidator>();
builder.Services.AddValidatorsFromAssemblyContaining<BookSessionValidator>();
builder.Services.AddValidatorsFromAssemblyContaining<CreatePurchaseValidator>();

builder.Services.AddApiRateLimiting();

// Singleton: the counter has to outlive the request, or every attempt would be the first one.
builder.Services.AddSingleton<PasswordResetRateLimiter>();

// --- authentication ------------------------------------------------------
// Bearer options are built from JwtOptions by ConfigureJwtBearerOptions, so signing and
// validation cannot drift onto different keys.
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireRole(nameof(UserRole.Admin)));
    options.AddPolicy("AthleteOnly", p => p.RequireRole(nameof(UserRole.Athlete)));

    // Deny by default — an endpoint must opt out with .AllowAnonymous().
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecurityTransformer>();
    options.AddSchemaTransformer<SchemaNormalizingTransformer>();
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<JsonExceptionHandler>();

var app = builder.Build();

var recurringJobs = app.Services.GetService<IRecurringJobManager>();
if (app.Configuration.GetValue("Jobs:Enabled", true) && recurringJobs is not null)
{
    recurringJobs.AddOrUpdate<CalendlySynchronizationJobs>(
        "calendly-reconciliation",
        job => job.ReconcileAsync(CancellationToken.None),
        $"*/{Math.Clamp(builder.Configuration.GetValue("Calendly:ReconciliationMinutes", 15), 5, 60)} * * * *");
}

// --- pipeline ------------------------------------------------------------
app.UseSerilogRequestLogging();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    // .NET 10 generates the OpenAPI document itself; it is served at /openapi/v1.json.
    // That file IS the contract handed to the Flutter developer (contract/openapi.yaml).
    // Anonymous, or the deny-by-default fallback policy locks the contract behind a token
    // you cannot get without reading the contract. Development only either way.
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();   // local UI at /scalar/v1
}

// Serves wwwroot, which holds the brand assets referenced by emails. A mail client fetches
// the logo over the public internet when the message is opened, so the file needs a real URL —
// committing it to the repository alone is not enough.
app.UseStaticFiles();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// After authorization, so it only ever runs for a request that got that far.
app.UseMiddleware<PausedAccountMiddleware>();

app.MapHealthChecks("/health").AllowAnonymous();

app.MapGet("/api/v1/ping", () => Results.Ok(new { message = "pong" })).AllowAnonymous();

app.MapAuthEndpoints();
app.MapInvitationEndpoints();
app.MapRegistrationEndpoints();
app.MapAthleteEndpoints();
app.MapPackageOptionEndpoints();
app.MapPricingEndpoints();
app.MapPreferenceEndpoints();
app.MapSchedulingEndpoints();
app.MapPurchasedPackageEndpoints();
app.MapPurchaseEndpoints();
app.MapAttendanceEndpoints();
app.MapSessionNoteEndpoints();
app.MapDashboardEndpoints();

if (app.Environment.IsDevelopment())
{
    // Development only. Staging and production apply migrations as a deliberate deploy
    // step, never on startup — an app that migrates itself can rewrite a database
    // nobody meant to touch.
    using (var scope = app.Services.CreateScope())
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

    await app.Services.SeedAdminAsync();
}

app.Run();

public partial class Program;   // lets integration tests start the app

using BeyondMovement.Api.Authentication;
using BeyondMovement.Api.Endpoints;
using BeyondMovement.Api.Middleware;
using BeyondMovement.Api.OpenApi;
using BeyondMovement.Api.Seeding;
using BeyondMovement.Infrastructure;
using BeyondMovement.Infrastructure.Auditing;
using BeyondMovement.Infrastructure.Email;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Features.ForgotPassword;
using BeyondMovement.Modules.Identity.Features.Login;
using BeyondMovement.Modules.Identity.Features.Logout;
using BeyondMovement.Modules.Identity.Features.Refresh;
using BeyondMovement.Modules.Identity.Features.ResetPassword;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;

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

// --- identity module -----------------------------------------------------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

// Only the vetted password-hashing part of ASP.NET Core Identity is used, not the full stack.
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<IEmailSender, ConsoleEmailSender>();

builder.Services.AddScoped<LoginHandler>();
builder.Services.AddScoped<RefreshHandler>();
builder.Services.AddScoped<LogoutHandler>();
builder.Services.AddScoped<ForgotPasswordHandler>();
builder.Services.AddScoped<ResetPasswordHandler>();

builder.Services.AddValidatorsFromAssemblyContaining<LoginValidator>();

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
    options.AddDocumentTransformer<BearerSecurityTransformer>());

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

builder.Services.AddProblemDetails();

var app = builder.Build();

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

app.UseAuthentication();
app.UseAuthorization();

// After authorization, so it only ever runs for a request that got that far.
app.UseMiddleware<PausedAccountMiddleware>();

app.MapHealthChecks("/health").AllowAnonymous();

app.MapGet("/api/v1/ping", () => Results.Ok(new { message = "pong" })).AllowAnonymous();

app.MapAuthEndpoints();

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

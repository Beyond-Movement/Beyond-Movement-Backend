using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Features.ForgotPassword;
using BeyondMovement.Modules.Identity.Features.Login;
using BeyondMovement.Modules.Identity.Features.Logout;
using BeyondMovement.Modules.Identity.Features.Refresh;
using BeyondMovement.Modules.Identity.Features.ResetPassword;
using BeyondMovement.Modules.Identity.Services;
using FluentValidation;

namespace BeyondMovement.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous by design: these are the endpoints you use before you have a token.
        // Everything else is denied by the fallback policy.
        var group = app.MapGroup("/api/v1/auth")
            .WithTags("Authentication")
            .AllowAnonymous();

        group.MapPost("/login", async (
            LoginRequest request,
            IValidator<LoginRequest> validator,
            LoginHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToDictionary().ToValidationProblem(http);

            var result = await handler.HandleAsync(request, ct);

            return result.IsSuccess
                ? Results.Ok(result.Value)
                : result.Error!.ToProblem(http);
        })
        .WithName("Login")
        .WithSummary("Exchange email and password for an access token and a refresh token.")
        .Produces<AuthResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status423Locked);

        group.MapPost("/refresh", async (
            RefreshRequest request,
            RefreshHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(request, ct);

            return result.IsSuccess
                ? Results.Ok(result.Value)
                : result.Error!.ToProblem(http);
        })
        .WithName("Refresh")
        .WithSummary("Rotate a refresh token for a new token pair. Reusing a spent token revokes the whole family.")
        .Produces<AuthResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/logout", async (
            LogoutRequest request,
            LogoutHandler handler,
            CancellationToken ct) =>
        {
            await handler.HandleAsync(request, ct);
            return Results.NoContent();
        })
        .WithName("Logout")
        .WithSummary("Revoke the presented refresh token.")
        .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/forgot-password", async (
            ForgotPasswordRequest request,
            IValidator<ForgotPasswordRequest> validator,
            ForgotPasswordHandler handler,
            IConfiguration configuration,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToDictionary().ToValidationProblem(http);

            var template = configuration["App:PasswordResetUrlTemplate"]!;
            await handler.HandleAsync(request, template, ct);

            // Always 200, whether or not the address exists.
            return Results.Ok();
        })
        .WithName("ForgotPassword")
        .WithSummary("Send a password reset link. Always succeeds, so it cannot be used to discover accounts.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/reset-password", async (
            ResetPasswordRequest request,
            IValidator<ResetPasswordRequest> validator,
            ResetPasswordHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToDictionary().ToValidationProblem(http);

            var result = await handler.HandleAsync(request, ct);

            return result.IsSuccess
                ? Results.Ok()
                : result.Error!.ToProblem(http);
        })
        .WithName("ResetPassword")
        .WithSummary("Set a new password using a reset token. Revokes every refresh token for that user.")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        MapCurrentUser(app);

        return app;
    }

    /// <summary>
    /// The smallest possible authenticated endpoint. It exists so the token pipeline can be
    /// verified end to end: 401 without a token, 200 with one, 403 once the account is paused.
    /// </summary>
    private static void MapCurrentUser(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/me", (ClaimsPrincipal principal) =>
        {
            var id = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            var role = principal.FindFirstValue(TokenService.RoleClaim);
            var coachId = principal.FindFirstValue(TokenService.CoachIdClaim);

            return Results.Ok(new { id, role, coachId });
        })
        .WithTags("Authentication")
        .WithName("CurrentUser")
        .WithSummary("Identity of the caller, taken from the access token.")
        .ProducesProblem(StatusCodes.Status401Unauthorized);
    }
}

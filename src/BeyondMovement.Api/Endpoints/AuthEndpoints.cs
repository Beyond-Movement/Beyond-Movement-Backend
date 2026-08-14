using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Features.ChangePassword;
using BeyondMovement.Modules.Identity.Features.CurrentUser;
using BeyondMovement.Modules.Identity.Features.ForgotPassword;
using BeyondMovement.Modules.Identity.Features.GoogleSignIn;
using BeyondMovement.Modules.Identity.Features.Login;
using BeyondMovement.Modules.Identity.Features.Logout;
using BeyondMovement.Modules.Identity.Features.Refresh;
using BeyondMovement.Modules.Identity.Features.ResetPassword;
using FluentValidation;

namespace BeyondMovement.Api.Endpoints;

public static class AuthEndpoints
{
    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        MapLogin(group);
        MapGoogleSignIn(group);
        MapRefresh(group);
        MapLogout(group);
        MapForgotPassword(group);
        MapResetPassword(group);
        MapChangePassword(group);
        MapCurrentUser(group);

        return app;
    }

    private static void MapLogin(RouteGroupBuilder group) =>
        group.MapPost("/login", async (
            LoginRequest request,
            IValidator<LoginRequest> validator,
            LoginHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            var result = await handler.HandleAsync(request, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .AllowAnonymous()
        .WithName("Login")
        .WithSummary("Exchange email and password for an access token and a refresh token.")
        .WithDescription(
            "Returns 401 INVALID_CREDENTIALS for both a wrong password and an unknown address — " +
            "the two are deliberately indistinguishable. Returns 423 ACCOUNT_LOCKED after five " +
            "failed attempts, with retryAfterSeconds and a Retry-After header. Returns 403 " +
            "ACCOUNT_PAUSED when the credentials are correct but the account is paused.")
        .Produces<AuthResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status423Locked, ProblemJson);

    private static void MapGoogleSignIn(RouteGroupBuilder group) =>
        group.MapPost("/google", async (
            GoogleSignInRequest request,
            IValidator<GoogleSignInRequest> validator,
            GoogleSignInHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            var result = await handler.HandleAsync(request, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .AllowAnonymous()
        .WithName("GoogleSignIn")
        .WithSummary("Sign in with a Google ID token obtained by the native sign-in on the device.")
        .WithDescription(
            "Authentication only — it never creates an account (BR-01). If the Google account is " +
            "unknown and no user exists with the same verified email, returns 403 " +
            "INVITATION_REQUIRED. If a password account already exists for that verified email, the " +
            "Google identity is linked to it and tokens are returned. Returns 401 " +
            "INVALID_GOOGLE_TOKEN when the token fails verification or the Google email is unverified.")
        .Produces<AuthResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapRefresh(RouteGroupBuilder group) =>
        group.MapPost("/refresh", async (
            RefreshRequest request,
            RefreshHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(request, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .AllowAnonymous()
        .WithName("Refresh")
        .WithSummary("Rotate a refresh token for a new token pair.")
        .WithDescription(
            "Refresh tokens are single-use. Each call returns a NEW refresh token; store it and " +
            "discard the old one. Replaying a token that was already spent is treated as theft: " +
            "every token in that family is revoked and 401 INVALID_REFRESH_TOKEN is returned, so " +
            "the user must sign in again. An expired or revoked token returns the same 401. " +
            "Returns 403 ACCOUNT_PAUSED if the account was paused since the token was issued.")
        .Produces<AuthResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapLogout(RouteGroupBuilder group) =>
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
        .WithDescription(
            "Requires a valid access token as well as the refresh token in the body " +
            "(architecture section 14.1 marks logout as authenticated). Succeeds even if the " +
            "refresh token is already unknown or revoked, so a retry is safe.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapForgotPassword(RouteGroupBuilder group) =>
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
                return validation.ToValidationProblem(http);

            await handler.HandleAsync(request, configuration["App:PasswordResetUrlTemplate"]!, ct);

            return Results.Ok();
        })
        .AllowAnonymous()
        .WithName("ForgotPassword")
        .WithSummary("Send a password reset link to the address, if an account exists.")
        .WithDescription(
            "Always returns 200, whether or not the address exists — it cannot be used to discover " +
            "accounts, so never show a 'no account found' message. When an account does exist, an " +
            "email is sent containing a deep link of the form " +
            "beyondmovement://reset-password?token=<token>. The token is single-use, expires after " +
            "one hour, and is URL-encoded in the link, so decode it before sending it to " +
            "/auth/reset-password.")
        .Produces(StatusCodes.Status200OK)
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson);

    private static void MapResetPassword(RouteGroupBuilder group) =>
        group.MapPost("/reset-password", async (
            ResetPasswordRequest request,
            IValidator<ResetPasswordRequest> validator,
            ResetPasswordHandler handler,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            var result = await handler.HandleAsync(request, ct);

            return result.IsSuccess ? Results.Ok() : result.Error!.ToProblem(http);
        })
        .AllowAnonymous()
        .WithName("ResetPassword")
        .WithSummary("Set a new password using the token from the reset email.")
        .WithDescription(
            "The token is single-use and valid for one hour; a used, unknown or expired token " +
            "returns 400 INVALID_RESET_TOKEN. On success every refresh token for that user is " +
            "revoked, so any other signed-in device is signed out. Passwords must be at least 8 " +
            "characters and are rejected if they appear on a common-password list; both arrive as " +
            "400 VALIDATION_FAILED with per-field messages in 'errors'.")
        .Produces(StatusCodes.Status200OK)
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson);

    private static void MapChangePassword(RouteGroupBuilder group) =>
        group.MapPost("/change-password", async (
            ChangePasswordRequest request,
            IValidator<ChangePasswordRequest> validator,
            ChangePasswordHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var result = await handler.HandleAsync(userId, request, ct);

            return result.IsSuccess ? Results.Ok() : result.Error!.ToProblem(http);
        })
        .WithName("ChangePassword")
        .WithSummary("Change the password while signed in.")
        .WithDescription(
            "Requires the current password. Returns 401 INVALID_CREDENTIALS if it is wrong, and " +
            "400 PASSWORD_NOT_SET for a Google-only account that has no password yet — those " +
            "accounts set a first password through Forgot Password. On success every refresh " +
            "token for the user is revoked, including this device's, so the app must sign in again.")
        .Produces(StatusCodes.Status200OK)
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapCurrentUser(RouteGroupBuilder group) =>
        group.MapGet("/me", async (
            CurrentUserHandler handler,
            ClaimsPrincipal principal,
            IConfiguration configuration,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var minimumVersion = configuration["App:MinimumSupportedAppVersion"] ?? "1.0.0";
            var result = await handler.HandleAsync(userId, minimumVersion, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .WithName("CurrentUser")
        .WithSummary("The signed-in user, for restoring a session on app start.")
        .WithDescription(
            "Read live from the database, not from the token, so a role or status changed since " +
            "the token was issued is reflected immediately. Returns 403 ACCOUNT_PAUSED if the " +
            "account was paused after this access token was issued — treat that as an immediate " +
            "sign-out. When profileCompleted is false, route the user to Complete Profile rather " +
            "than Home. minimumSupportedAppVersion drives the forced-upgrade prompt.")
        .Produces<CurrentUserResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static bool TryGetUserId(this ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out userId);
}

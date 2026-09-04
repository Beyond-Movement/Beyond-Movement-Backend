using System.Security.Claims;
using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Features.ChangePassword;
using BeyondMovement.Modules.Identity.Features.CurrentUser;
using BeyondMovement.Modules.Identity.Features.ForgotPassword;
using BeyondMovement.Modules.Identity.Features.GoogleSignIn;
using BeyondMovement.Modules.Identity.Features.Login;
using BeyondMovement.Modules.Identity.Features.Logout;
using BeyondMovement.Modules.Identity.Features.Profile;
using BeyondMovement.Modules.Identity.Features.TimeZone;
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
        MapUpdateTimeZone(group);
        MapGetProfile(group);
        MapUpdateProfile(group);

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
            "ACCOUNT_PAUSED when the credentials are correct but the account is paused. " +
            "user.profileCompleted tells the app where to go next without a further call: " +
            "false means route to Complete Profile rather than Home.")
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
            PasswordResetRateLimiter perEmail,
            IConfiguration configuration,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            // Before the handler, and so before any database lookup: the limit must count the
            // address that was submitted, not the accounts that exist. Checking only real
            // accounts would make a 429 mean "this address is registered".
            if (perEmail.Check(request.Email) is { } limited)
                return limited.ToProblem(http);

            await handler.HandleAsync(request, configuration["App:PasswordResetUrlTemplate"]!, ct);

            return Results.Ok();
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.PasswordReset)
        .WithName("ForgotPassword")
        .WithSummary("Send a password reset link to the address, if an account exists.")
        .WithDescription(
            "Always returns 200, whether or not the address exists — it cannot be used to discover " +
            "accounts, so never show a 'no account found' message. When an account does exist, an " +
            "email is sent containing a deep link of the form " +
            "beyondmovement://reset-password?token=<token>. The token is single-use, expires after " +
            "one hour, and is URL-encoded in the link, so decode it before sending it to " +
            "/auth/reset-password. " +
            "Rate-limited on two axes: 3 requests per hour per email address, and 10 per hour " +
            "per IP. Either limit returns 429 TOO_MANY_REQUESTS with retryAfterSeconds in the " +
            "body and a Retry-After header, both in seconds and both up to 3600 - show minutes, " +
            "not seconds. The per-email limit counts the address that was submitted, whether or " +
            "not an account exists, so a 429 still reveals nothing about who is registered: " +
            "treat it exactly like the 200, as 'we have sent a link if that address is known'.")
        .Produces(StatusCodes.Status200OK)
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, ProblemJson);

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

    /// <summary>
    /// Device time-zone synchronisation. Not a setting — see the endpoint description.
    /// </summary>
    private static void MapUpdateTimeZone(RouteGroupBuilder group) =>
        group.MapPut("/me/timezone", async (
            UpdateTimeZoneRequest request,
            UpdateTimeZoneHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var result = await handler.HandleAsync(userId, request, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .WithName("UpdateTimeZone")
        .WithSummary("Keep the stored time zone in step with the device's.")
        .WithDescription(
            "There is NO time-zone setting in the app and the user never chooses one. On start-up " +
            "the app detects the device zone, compares it with the timeZone field from " +
            "GET /api/v1/auth/me, and calls this ONLY when the two differ - so in normal use it " +
            "runs once, when the device has actually moved. " +
            "Send an IANA id such as Africa/Cairo; a Windows id is accepted, but IANA is what a " +
            "mobile platform reports and what /auth/me returns for comparison. The value is stored " +
            "and returned exactly as sent, so a later /auth/me matches what was written and the " +
            "comparison settles after one call. " +
            "400 TIME_ZONE_INVALID for anything this server cannot resolve - it is refused rather " +
            "than ignored, because the dashboard's own resolver falls back to UTC silently and a " +
            "bad value would otherwise show up only as wrong figures. " +
            "Repeating the same zone is a no-op that still returns 200. " +
            "This is what the ADMIN dashboard computes week, month and year boundaries in; an " +
            "athlete may call it and the value is stored, but nothing reads it for them yet.")
        .Produces<TimeZoneResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapGetProfile(RouteGroupBuilder group) =>
        group.MapGet("/me/profile", async (
            AdminProfileHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var result = await handler.GetAsync(userId, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .RequireAuthorization("AdminOnly")
        .WithName("GetMyProfile")
        .WithSummary("The signed-in Admin's own profile.")
        .WithDescription(
            "Personal Information: full name, email and phone, and deliberately nothing else - " +
            "no profile picture and no professional title exist in this API. " +
            "Separate from /auth/me on purpose: that endpoint answers who is signed in and where " +
            "to route them, is called on every app start, and must not grow contact details. " +
            "Read this when the Profile screen opens. " +
            "phone is null until somebody sets one - no screen has ever written it. " +
            "email is READ-ONLY here and is not accepted by the PUT.")
        .Produces<AdminProfileResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

    private static void MapUpdateProfile(RouteGroupBuilder group) =>
        group.MapPut("/me/profile", async (
            UpdateAdminProfileRequest request,
            IValidator<UpdateAdminProfileRequest> validator,
            AdminProfileHandler handler,
            ClaimsPrincipal principal,
            HttpContext http,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
                return validation.ToValidationProblem(http);

            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var result = await handler.UpdateAsync(userId, request, ct);

            return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem(http);
        })
        .RequireAuthorization("AdminOnly")
        .WithName("UpdateMyProfile")
        .WithSummary("Edit the signed-in Admin's own name and phone.")
        .WithDescription(
            "A full replacement of both editable fields, not a patch: send fullName and phone " +
            "every time. Always the caller's own profile - there is no id in the route or body. " +
            "fullName is required; blank is 400 VALIDATION_FAILED. " +
            "phone is optional - send null or an empty string to clear it, and it reads back as " +
            "null either way. Digits and + ( ) - . only, up to 40 characters; the format is " +
            "otherwise unconstrained because numbers are international and displayed, not dialled. " +
            "EMAIL CANNOT BE CHANGED HERE. It is the login identity and the unique key on the " +
            "user, so changing it needs re-verification and re-issued tokens - a feature of its " +
            "own. It is absent from this request and untouched by this call. " +
            "The response is the profile as stored, after trimming, so render from it rather " +
            "than from what was sent.")
        .Produces<AdminProfileResponse>()
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
            "than Home. minimumSupportedAppVersion drives the forced-upgrade prompt. " +
            "timeZone is the zone currently stored for this user, exactly as it was last " +
            "written - compare it with the device's detected zone on start-up and call " +
            "PUT /api/v1/auth/me/timezone only when they differ. It is session state, not a " +
            "profile field: contact details live on /auth/me/profile instead.")
        .Produces<CurrentUserResponse>()
        .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
        .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);
}

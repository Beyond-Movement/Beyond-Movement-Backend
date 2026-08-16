using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Modules.Identity.Features.Register;

/// <param name="CoachId">Needed by the caller to create the matching athlete profile.</param>
public sealed record RegistrationResult(Guid UserId, Guid CoachId, AuthResponse Auth);

/// <summary>
/// Creates the account behind a validated invitation and redeems it.
/// <para>
/// This handler owns the identity half only. The caller wraps it in a transaction together
/// with athlete-profile creation, because modules must not reference one another
/// (CLAUDE.md section 4) yet "a valid invitation creates exactly one athlete account" has to
/// hold across both.
/// </para>
/// </summary>
public sealed class RegisterHandler(
    IIdentityDbContext db,
    IPasswordHasher<User> passwordHasher,
    IGoogleTokenValidator googleTokens,
    IRegistrationTokenService registrationTokens,
    ITokenService tokens,
    IAuditLogger audit,
    IClock clock,
    IOptions<JwtOptions> jwtOptions,
    ILogger<RegisterHandler> logger)
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<Result<RegistrationResult>> HandleAsync(
        RegisterRequest request, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        if (!request.TermsAccepted)
            return Result<RegistrationResult>.Failure(IdentityErrors.TermsNotAccepted);

        var ticket = registrationTokens.Validate(request.RegistrationToken);

        if (ticket is null)
            return Result<RegistrationResult>.Failure(IdentityErrors.RegistrationTokenInvalid);

        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == ticket.InvitationId, ct);

        // Re-checked here rather than trusting the ticket: the invitation may have been revoked,
        // redeemed, or expired in the minutes since it was validated.
        if (invitation is null)
            return Result<RegistrationResult>.Failure(IdentityErrors.InvitationInvalid);

        if (invitation.Status == InvitationStatus.Redeemed)
            return Result<RegistrationResult>.Failure(IdentityErrors.InvitationUsed);

        if (invitation.Status == InvitationStatus.Revoked)
            return Result<RegistrationResult>.Failure(IdentityErrors.InvitationRevoked);

        if (invitation.IsExpired(now))
            return Result<RegistrationResult>.Failure(IdentityErrors.InvitationExpired);

        if (await db.Users.AnyAsync(u => u.Email == invitation.Email && u.Status != UserStatus.Deleted, ct))
            return Result<RegistrationResult>.Failure(IdentityErrors.EmailAlreadyRegistered);

        var credentials = await ResolveCredentialsAsync(request, invitation.Email, ct);

        if (credentials.IsFailure)
            return Result<RegistrationResult>.Failure(credentials.Error!);

        var (_, googleSubject, googleName) = credentials.Value;

        var rawPassword = credentials.Value.RawPassword;

        var user = User.CreateAthlete(
            invitation.Email,
            // Google's display name is kept as a prefill for Complete Profile, where the
            // athlete confirms it. The password path has no name to offer, and does not ask.
            googleName,
            // A placeholder, replaced immediately below: PasswordHasher salts per call and
            // needs a User instance, so the real hash cannot be produced before this point.
            passwordHash: rawPassword is null ? null : "placeholder",
            googleSubjectId: googleSubject,
            invitation.CoachId,
            now);

        if (rawPassword is not null)
            user.SetPasswordHash(passwordHasher.HashPassword(user, rawPassword), now);

        db.Users.Add(user);
        invitation.Redeem(now);

        var (rawRefresh, refreshHash) = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(RefreshToken.Issue(
            user.Id, refreshHash, familyId: Guid.NewGuid(), deviceId: null, now, _jwt.RefreshTokenDays));

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("InvitationRedeemed", user.Id,
            $"Invitation {invitation.Id} redeemed; athlete account created.", ct);

        logger.LogInformation("Athlete {UserId} registered from invitation {InvitationId}",
            user.Id, invitation.Id);

        return Result<RegistrationResult>.Success(new RegistrationResult(
            user.Id,
            invitation.CoachId,
            AuthResponseFactory.Create(user, tokens, rawRefresh, _jwt)));
    }

    /// <summary>
    /// Exactly one of password or Google must be supplied. The raw password is returned rather
    /// than a hash because <see cref="PasswordHasher{T}"/> salts per user, so it cannot be
    /// hashed until the user object exists.
    /// </summary>
    private async Task<Result<(string? RawPassword, string? GoogleSubject, string? GoogleName)>>
        ResolveCredentialsAsync(RegisterRequest request, string invitedEmail, CancellationToken ct)
    {
        var hasPassword = !string.IsNullOrWhiteSpace(request.Password);
        var hasGoogle = !string.IsNullOrWhiteSpace(request.GoogleIdToken);

        if (hasPassword == hasGoogle)
        {
            return Result<(string?, string?, string?)>.Failure(new Error(
                ApiErrorCodes.ValidationFailed,
                "Supply either a password or a Google ID token, not both and not neither.",
                400));
        }

        if (hasPassword)
            return Result<(string?, string?, string?)>.Success((request.Password, null, null));

        var identity = await googleTokens.ValidateAsync(request.GoogleIdToken!, ct);

        if (identity is null || !identity.EmailVerified)
            return Result<(string?, string?, string?)>.Failure(IdentityErrors.InvalidGoogleToken);

        // "Each invitation can be used only for its intended athlete" — a Google account with a
        // different address cannot redeem someone else's invitation.
        if (!string.Equals(identity.Email, invitedEmail, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Registration refused: Google email does not match the invited address");
            return Result<(string?, string?, string?)>.Failure(IdentityErrors.GoogleEmailMismatch);
        }

        return Result<(string?, string?, string?)>.Success((null, identity.Subject, identity.FullName));
    }
}

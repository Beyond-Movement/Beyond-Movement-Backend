using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Identity.Features.Invitations;

/// <summary>
/// Exchanges a valid code for a registration ticket. The invitation is <b>not</b> consumed —
/// redemption happens only when the account is created, so an abandoned Create Account screen
/// does not burn the athlete's invitation.
/// </summary>
public sealed class ValidateInvitationHandler(
    IIdentityDbContext db,
    ITokenService tokens,
    IRegistrationTokenService registrationTokens,
    IClock clock)
{
    public async Task<Result<ValidateInvitationResponse>> HandleAsync(string code, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var hash = tokens.Hash(InvitationCode.Normalize(code));

        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.CodeHash == hash, ct);

        // Each case gets its own code: the Invitation Error screen shows a different message
        // and a different next step for each.
        if (invitation is null)
            return Result<ValidateInvitationResponse>.Failure(IdentityErrors.InvitationInvalid);

        if (invitation.Status == InvitationStatus.Redeemed)
            return Result<ValidateInvitationResponse>.Failure(IdentityErrors.InvitationUsed);

        if (invitation.Status == InvitationStatus.Revoked)
            return Result<ValidateInvitationResponse>.Failure(IdentityErrors.InvitationRevoked);

        if (invitation.IsExpired(now))
            return Result<ValidateInvitationResponse>.Failure(IdentityErrors.InvitationExpired);

        invitation.RecordValidated(now);
        await db.SaveChangesAsync(ct);

        return Result<ValidateInvitationResponse>.Success(new ValidateInvitationResponse(
            invitation.Email,
            invitation.ExpiresAtUtc,
            registrationTokens.Issue(invitation.Id, invitation.Email),
            RegistrationTokenService.LifetimeMinutes * 60));
    }
}

using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeyondMovement.Modules.Identity.Features.Invitations;

public sealed class ManageInvitationsHandler(
    IIdentityDbContext db,
    ITokenService tokens,
    IEmailSender email,
    IAuditLogger audit,
    IClock clock,
    ILogger<ManageInvitationsHandler> logger)
{
    public async Task<IReadOnlyList<InvitationResponse>> ListAsync(
        Guid coachId, InvitationStatus? status, CancellationToken ct = default)
    {
        var query = db.Invitations.AsNoTracking().Where(i => i.CoachId == coachId);

        if (status is not null)
            query = query.Where(i => i.Status == status);

        return await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => new InvitationResponse(
                i.Id, i.Email, i.Status, i.ExpiresAtUtc, i.CreatedAtUtc, i.RedeemedAtUtc, i.SendCount))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Issues a fresh code and emails it again. The previous code stops working, so a resend
    /// cannot leave two live codes for one athlete.
    /// </summary>
    public async Task<Result<InvitationResponse>> ResendAsync(
        Guid coachId, Guid invitationId, Guid adminUserId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        var invitation = await FindAsync(coachId, invitationId, ct);

        if (invitation is null)
            return Result<InvitationResponse>.Failure(IdentityErrors.InvitationInvalid);

        if (invitation.Status == InvitationStatus.Redeemed)
            return Result<InvitationResponse>.Failure(IdentityErrors.InvitationUsed);

        if (invitation.Status == InvitationStatus.Revoked)
            return Result<InvitationResponse>.Failure(IdentityErrors.InvitationRevoked);

        var rawCode = InvitationCode.Generate();
        invitation.ReplaceCode(tokens.Hash(InvitationCode.Normalize(rawCode)), now);

        await db.SaveChangesAsync(ct);

        await email.SendAsync(
            invitation.Email,
            "Your Beyond Movement invitation code",
            $"""
             Here is your invitation code: {rawCode}

             Open the app, choose "Enter invitation code", and enter it before {invitation.ExpiresAtUtc:d MMMM yyyy}.
             Any code you received earlier no longer works.
             """,
            ct);

        await audit.WriteAsync("InvitationResent", adminUserId, $"Invitation {invitation.Id} resent.", ct);
        logger.LogInformation("Invitation {InvitationId} resent (send #{SendCount})",
            invitation.Id, invitation.SendCount);

        return Result<InvitationResponse>.Success(invitation.ToResponse());
    }

    public async Task<Result> RevokeAsync(
        Guid coachId, Guid invitationId, Guid adminUserId, CancellationToken ct = default)
    {
        var invitation = await FindAsync(coachId, invitationId, ct);

        if (invitation is null)
            return Result.Failure(IdentityErrors.InvitationInvalid);

        // Revoking an already-redeemed invitation would suggest it undoes the account. It does not.
        if (invitation.Status == InvitationStatus.Redeemed)
            return Result.Failure(IdentityErrors.InvitationUsed);

        invitation.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync("InvitationRevoked", adminUserId, $"Invitation {invitation.Id} revoked.", ct);
        logger.LogInformation("Invitation {InvitationId} revoked", invitation.Id);

        return Result.Success();
    }

    // Scoped to the coach: another coach's invitation is simply not found (CLAUDE.md section 6).
    private Task<Invitation?> FindAsync(Guid coachId, Guid invitationId, CancellationToken ct) =>
        db.Invitations.FirstOrDefaultAsync(i => i.Id == invitationId && i.CoachId == coachId, ct);
}

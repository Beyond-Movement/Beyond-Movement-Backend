using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Persistence;
using BeyondMovement.Modules.Identity.Services;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeyondMovement.Modules.Identity.Features.Invitations;

public sealed class CreateInvitationHandler(
    IIdentityDbContext db,
    ITokenService tokens,
    IEmailSender email,
    IAuditLogger audit,
    IClock clock,
    ILogger<CreateInvitationHandler> logger)
{
    public async Task<Result<InvitationResponse>> HandleAsync(
        Guid adminUserId, Guid coachId, CreateInvitationRequest request, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var address = request.Email.ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == address && u.Status != UserStatus.Deleted, ct))
            return Result<InvitationResponse>.Failure(IdentityErrors.EmailAlreadyRegistered);

        // Re-inviting an address that already has a live invitation replaces it rather than
        // creating a second one, so the athlete's inbox never holds two working codes.
        var existing = await db.Invitations
            .FirstOrDefaultAsync(i => i.Email == address && i.Status == InvitationStatus.Pending, ct);

        var rawCode = InvitationCode.Generate();
        var codeHash = tokens.Hash(InvitationCode.Normalize(rawCode));

        Invitation invitation;

        if (existing is not null)
        {
            existing.ReplaceCode(codeHash, now);
            invitation = existing;
        }
        else
        {
            invitation = Invitation.Create(address, codeHash, coachId, adminUserId, now);
            db.Invitations.Add(invitation);
        }

        await db.SaveChangesAsync(ct);

        await SendInvitationEmailAsync(address, rawCode, invitation.ExpiresAtUtc, ct);

        await audit.WriteAsync("InvitationSent", adminUserId, $"Invitation {invitation.Id} issued.", ct);

        // The address is deliberately absent from the log line (CLAUDE.md section 7).
        logger.LogInformation("Invitation {InvitationId} created, expiring {ExpiresAtUtc:u}",
            invitation.Id, invitation.ExpiresAtUtc);

        return Result<InvitationResponse>.Success(invitation.ToResponse());
    }

    private Task SendInvitationEmailAsync(string address, string rawCode, DateTime expiresAtUtc, CancellationToken ct) =>
        email.SendAsync(
            address,
            "You have been invited to Beyond Movement",
            $"""
             Your coach has invited you to join Beyond Movement.

             Your invitation code is: {rawCode}

             Open the app, choose "Enter invitation code", and enter it before {expiresAtUtc:d MMMM yyyy}.
             """,
            ct);
}

internal static class InvitationMappings
{
    public static InvitationResponse ToResponse(this Invitation invitation) => new(
        invitation.Id,
        invitation.Email,
        invitation.Status,
        invitation.ExpiresAtUtc,
        invitation.CreatedAtUtc,
        invitation.RedeemedAtUtc,
        invitation.SendCount);
}

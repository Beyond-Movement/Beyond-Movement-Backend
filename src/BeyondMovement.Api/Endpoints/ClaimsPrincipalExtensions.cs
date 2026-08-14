using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BeyondMovement.Modules.Identity.Services;

namespace BeyondMovement.Api.Endpoints;

public static class ClaimsPrincipalExtensions
{
    public static bool TryGetUserId(this ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out userId);

    /// <summary>
    /// Both ids come from the token, never from the request body — a caller must not be able to
    /// name a different coach and reach another coach's data.
    /// </summary>
    public static bool TryGetIdentity(this ClaimsPrincipal principal, out Guid userId, out Guid coachId)
    {
        coachId = Guid.Empty;

        return principal.TryGetUserId(out userId)
            && Guid.TryParse(principal.FindFirstValue(TokenService.CoachIdClaim), out coachId);
    }
}

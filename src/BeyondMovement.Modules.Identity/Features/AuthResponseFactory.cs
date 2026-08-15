using BeyondMovement.Modules.Identity.Contracts;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.Modules.Identity.Services;

namespace BeyondMovement.Modules.Identity.Features;

/// <summary>
/// One place that builds the successful-authentication payload, so login, refresh and Google
/// sign-in cannot drift apart in what they return.
/// </summary>
internal static class AuthResponseFactory
{
    public static AuthResponse Create(User user, ITokenService tokens, string rawRefreshToken, JwtOptions jwt) =>
        new(
            tokens.CreateAccessToken(user),
            rawRefreshToken,
            jwt.AccessTokenMinutes * 60,
            jwt.RefreshTokenDays * 24 * 60 * 60,
            new UserSummary(user.Id, user.Role, user.Status, user.FullName, user.Email,
                user.ProfileCompleted, user.AthleteListSort));
}

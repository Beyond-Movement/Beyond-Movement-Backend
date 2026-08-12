using BeyondMovement.Modules.Identity.Domain;

namespace BeyondMovement.Modules.Identity.Services;

public interface ITokenService
{
    /// <summary>A signed JWT carrying sub, role, coachId, jti and exp.</summary>
    string CreateAccessToken(User user);

    /// <summary>
    /// A new refresh token. The raw value goes to the caller and is never stored;
    /// only the hash is persisted.
    /// </summary>
    (string raw, string hash) CreateRefreshToken();

    /// <summary>SHA-256 of a raw token, for looking up the stored hash.</summary>
    string Hash(string raw);
}

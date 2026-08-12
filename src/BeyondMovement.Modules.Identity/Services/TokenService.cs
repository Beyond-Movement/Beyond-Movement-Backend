using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BeyondMovement.Modules.Identity.Domain;
using BeyondMovement.SharedKernel;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BeyondMovement.Modules.Identity.Services;

public sealed class TokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    public const string RoleClaim = "role";
    public const string CoachIdClaim = "coachId";

    private readonly JwtOptions _options = options.Value;

    public string CreateAccessToken(User user)
    {
        var now = clock.UtcNow;

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(RoleClaim, user.Role.ToString()),
            new Claim(CoachIdClaim, user.CoachId.ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public (string raw, string hash) CreateRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    // Refresh tokens are hashed for the same reason passwords are: if the database
    // leaks, the tokens inside it must be useless.
    public string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}

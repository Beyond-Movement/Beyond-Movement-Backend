using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BeyondMovement.SharedKernel;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BeyondMovement.Modules.Identity.Services;

public sealed record RegistrationTicket(Guid InvitationId, string Email);

/// <summary>
/// Issues the short-lived token that stands between "the code is valid" and "the account
/// exists". Validating a code does not consume it, so the API hands back this ticket instead:
/// it names the invitation, cannot be edited by the client, and expires quickly.
/// </summary>
public interface IRegistrationTokenService
{
    string Issue(Guid invitationId, string email);
    RegistrationTicket? Validate(string token);
}

public sealed class RegistrationTokenService(IOptions<JwtOptions> options, IClock clock)
    : IRegistrationTokenService
{
    /// <summary>
    /// A distinct audience so a registration token can never be presented as an access token,
    /// or the reverse — they are signed with the same key.
    /// </summary>
    public const string Audience = "beyond-movement-registration";
    public const int LifetimeMinutes = 30;

    private const string InvitationClaim = "invitationId";

    private readonly JwtOptions _options = options.Value;

    public string Issue(Guid invitationId, string email)
    {
        var now = clock.UtcNow;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: Audience,
            claims:
            [
                new Claim(InvitationClaim, invitationId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ],
            notBefore: now,
            expires: now.AddMinutes(LifetimeMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public RegistrationTicket? Validate(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _options.Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        try
        {
            // MapInboundClaims off: the default rewrites "email" to the long WS-Federation
            // claim URI, so reading it back by its own name would silently find nothing.
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

            var principal = handler.ValidateToken(token, parameters, out _);

            var invitationId = principal.FindFirstValue(InvitationClaim);
            var email = principal.FindFirstValue(JwtRegisteredClaimNames.Email);

            if (!Guid.TryParse(invitationId, out var id) || string.IsNullOrWhiteSpace(email))
                return null;

            return new RegistrationTicket(id, email);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            // Expired, tampered with, or issued for something else. Not exceptional.
            return null;
        }
    }
}

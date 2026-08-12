using System.IdentityModel.Tokens.Jwt;
using System.Text;
using BeyondMovement.Modules.Identity.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BeyondMovement.Api.Authentication;

/// <summary>
/// Validation parameters are built from the same <see cref="JwtOptions"/> that
/// <see cref="TokenService"/> signs with.
/// <para>
/// Reading the key straight off <c>builder.Configuration</c> at startup instead would capture
/// whatever value existed at that moment, while the token service resolves its options later —
/// so the two halves could end up on different keys and every token would fail validation.
/// </para>
/// </summary>
public sealed class ConfigureJwtBearerOptions(IOptions<JwtOptions> jwtOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public void Configure(JwtBearerOptions options)
    {
        if (string.IsNullOrWhiteSpace(_jwt.SigningKey))
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is not configured. Set it with: dotnet user-secrets set " +
                "\"Jwt:SigningKey\" \"<64+ character value>\" --project src/BeyondMovement.Api");
        }

        // Off by intent: the default remaps "sub" to the long WS-Federation claim URI,
        // so code reading "sub" would silently find nothing.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _jwt.Issuer,
            ValidAudience = _jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),

            // Read the claims exactly as TokenService writes them, with no renaming.
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = TokenService.RoleClaim
        };
    }

    public void Configure(string? name, JwtBearerOptions options) => Configure(options);
}

using BeyondMovement.Modules.Identity.Services;
using Google.Apis.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeyondMovement.Infrastructure.Google;

/// <summary>
/// Verifies Google ID tokens using Google's official library, which fetches and caches the
/// JWKS and checks the signature, issuer, audience and expiry.
/// <para>
/// The identity is never taken from what the client claims — only from a token that survives
/// this verification (architecture section 12).
/// </para>
/// </summary>
public sealed class GoogleTokenValidator(
    IOptions<GoogleOptions> options,
    ILogger<GoogleTokenValidator> logger) : IGoogleTokenValidator
{
    private readonly GoogleOptions _options = options.Value;

    public async Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken ct = default)
    {
        var audiences = _options.AllClientIds.ToList();

        if (audiences.Count == 0)
        {
            // Without a configured audience the token's "aud" cannot be checked, and a token
            // minted for any other Google app would sail through. Refuse instead.
            logger.LogError(
                "Google sign-in attempted but no Google:ClientId values are configured. " +
                "Set Google:ClientId:Web, :Android and :iOS.");
            return null;
        }

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = audiences
            });

            return new GoogleIdentity(
                payload.Subject,
                payload.Email,
                payload.EmailVerified,
                payload.Name);
        }
        catch (InvalidJwtException ex)
        {
            // Expected for an expired, forged, or wrong-audience token. Not exceptional.
            logger.LogInformation("Google ID token rejected: {Reason}", ex.Message);
            return null;
        }
    }
}

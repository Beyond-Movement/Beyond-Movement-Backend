namespace BeyondMovement.Modules.Identity.Services;

/// <param name="Subject">Google's stable user id — the "sub" claim. Never the email, which can change.</param>
public sealed record GoogleIdentity(string Subject, string Email, bool EmailVerified, string? FullName);

/// <summary>
/// Verifies a Google ID token against Google's published keys. Implemented in Infrastructure
/// because it calls an external service; the module depends only on this abstraction.
/// </summary>
public interface IGoogleTokenValidator
{
    /// <returns>The verified identity, or null when the token fails validation.</returns>
    Task<GoogleIdentity?> ValidateAsync(string idToken, CancellationToken ct = default);
}

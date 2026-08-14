using BeyondMovement.Modules.Identity.Services;

namespace BeyondMovement.UnitTests.Identity;

public class EmailTemplateTests
{
    private static readonly DateTime Expiry = new(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_invitation_carries_the_code_in_both_bodies()
    {
        var message = EmailTemplates.Invitation("athlete@example.com", "MRPZB-AXZYY", Expiry);

        // A client that refuses HTML must still be able to read the code, or the athlete is stuck.
        Assert.Contains("MRPZB-AXZYY", message.HtmlBody);
        Assert.Contains("MRPZB-AXZYY", message.TextBody);
    }

    [Fact]
    public void An_invitation_states_when_it_expires()
    {
        var message = EmailTemplates.Invitation("athlete@example.com", "MRPZB-AXZYY", Expiry);

        Assert.Contains("28 August 2026", message.HtmlBody);
        Assert.Contains("28 August 2026", message.TextBody);
    }

    [Fact]
    public void A_resend_says_the_previous_code_is_dead()
    {
        var resend = EmailTemplates.Invitation("athlete@example.com", "NEWCO-DE123", Expiry, isResend: true);

        // Otherwise the recipient has two codes in their inbox and no way to tell which works.
        Assert.Contains("no longer works", resend.TextBody);
        Assert.NotEqual(
            EmailTemplates.Invitation("athlete@example.com", "NEWCO-DE123", Expiry).Subject,
            resend.Subject);
    }

    [Fact]
    public void A_password_reset_carries_the_link_and_its_lifetime()
    {
        const string link = "beyondmovement://reset-password?token=abc123%3D";

        var message = EmailTemplates.PasswordReset("athlete@example.com", link, 1);

        Assert.Contains(link, message.TextBody);
        Assert.Contains("1 hour", message.TextBody);
        Assert.Contains("1 hour", message.HtmlBody);
    }

    [Fact]
    public void A_password_reset_tells_the_reader_what_to_do_if_they_did_not_ask_for_it()
    {
        var message = EmailTemplates.PasswordReset("athlete@example.com", "beyondmovement://x", 1);

        // Someone who did not request a reset should be reassured, not alarmed into acting.
        Assert.Contains("did not request", message.TextBody);
        Assert.Contains("did not request", message.HtmlBody);
    }

    [Fact]
    public void Both_bodies_are_always_present()
    {
        EmailMessage[] messages =
        [
            EmailTemplates.Invitation("a@example.com", "CODE1-CODE2", Expiry),
            EmailTemplates.Invitation("a@example.com", "CODE1-CODE2", Expiry, isResend: true),
            EmailTemplates.PasswordReset("a@example.com", "beyondmovement://x", 1)
        ];

        foreach (var message in messages)
        {
            // A message with no plain-text part scores worse with spam filters.
            Assert.False(string.IsNullOrWhiteSpace(message.HtmlBody));
            Assert.False(string.IsNullOrWhiteSpace(message.TextBody));
            Assert.False(string.IsNullOrWhiteSpace(message.Subject));
            Assert.Contains("<!DOCTYPE html>", message.HtmlBody);
            Assert.DoesNotContain("<table", message.TextBody);
        }
    }

    [Fact]
    public void With_no_logo_configured_the_masthead_is_type_not_an_image()
    {
        var message = EmailTemplates.Invitation("a@example.com", "CODE1-CODE2", Expiry);

        Assert.DoesNotContain("<img", message.HtmlBody);
        Assert.Contains("Beyond Movement", message.HtmlBody);

        // An external stylesheet is stripped by every mail client, so nothing may depend on one.
        Assert.DoesNotContain("<link", message.HtmlBody);
    }

    [Fact]
    public void A_configured_logo_is_referenced_by_url_with_explicit_dimensions()
    {
        const string logo = "https://api.beyondmovement.com/brand/logo.png";

        var message = EmailTemplates.Invitation("a@example.com", "CODE1-CODE2", Expiry, logoUrl: logo);

        Assert.Contains(logo, message.HtmlBody);

        // Explicit width and height stop Outlook reflowing the masthead before the image loads,
        // and alt text carries the brand when images are switched off.
        Assert.Contains("width=\"132\"", message.HtmlBody);
        Assert.Contains("height=\"132\"", message.HtmlBody);
        Assert.Contains("alt=\"Beyond Movement\"", message.HtmlBody);
    }

    [Fact]
    public void The_plain_text_body_never_depends_on_the_logo()
    {
        var withLogo = EmailTemplates.Invitation("a@example.com", "CODE1-CODE2", Expiry,
            logoUrl: "https://api.beyondmovement.com/brand/logo.png");
        var without = EmailTemplates.Invitation("a@example.com", "CODE1-CODE2", Expiry);

        // Whatever happens to the image, the code still has to reach the reader.
        Assert.Equal(without.TextBody, withLogo.TextBody);
        Assert.Contains("CODE1-CODE2", withLogo.TextBody);
    }
}

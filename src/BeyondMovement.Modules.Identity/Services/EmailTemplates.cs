using System.Net;

namespace BeyondMovement.Modules.Identity.Services;

/// <summary>
/// The wording and markup of every email this module sends, in one place so copy can be
/// reviewed without reading handler code.
/// <para>
/// Email HTML is not web HTML. Layout is table-based with inline styles because Outlook
/// ignores most of a stylesheet, the width is capped at 600px, and there are no external
/// images — remote images are blocked by default in most clients, so anything that matters
/// is text.
/// </para>
/// <para>
/// The brand mark is set as type rather than an image for the same reason. Swap in a real
/// logo only once it can be hosted on a stable HTTPS URL.
/// </para>
/// </summary>
public static class EmailTemplates
{
    // Taken from the Beyond Movement logo: a royal-blue wordmark on a pale yellow disc.
    private const string Brand = "#3A4BA8";
    private const string BrandTint = "#F8F3B2";
    private const string Ink = "#1A1A2E";
    private const string Muted = "#6B7280";
    private const string Line = "#E5E7EB";
    private const string Canvas = "#F4F4F7";

    /// <param name="logoUrl">
    /// Absolute HTTPS address of the logo. When empty the masthead falls back to the wordmark
    /// set as type. A data: URI will not do — Gmail strips them, so the logo would vanish for
    /// most recipients while looking fine in testing.
    /// </param>
    public static EmailMessage Invitation(
        string to, string code, DateTime expiresAtUtc, bool isResend = false, string? logoUrl = null)
    {
        var subject = isResend
            ? "Your new Beyond Movement invitation code"
            : "You have been invited to Beyond Movement";

        var opening = isResend
            ? "Here is your new invitation code. Any code you received earlier no longer works."
            : "Your coach has invited you to join Beyond Movement.";

        var expiry = expiresAtUtc.ToString("d MMMM yyyy");

        var body = $"""
            <p style="margin:0 0 24px;font-size:16px;line-height:24px;color:{Ink};">{Encode(opening)}</p>

            <p style="margin:0 0 12px;font-size:14px;line-height:20px;color:{Muted};">Your invitation code</p>

            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 24px;">
              <tr>
                <td style="background:{Canvas};border:1px solid {Line};border-radius:8px;padding:18px 28px;
                           font-family:'SFMono-Regular',Consolas,'Liberation Mono',Menlo,monospace;
                           font-size:26px;letter-spacing:3px;font-weight:700;color:{Ink};">
                  {Encode(code)}
                </td>
              </tr>
            </table>

            <p style="margin:0 0 8px;font-size:16px;line-height:24px;color:{Ink};">
              Open the Beyond Movement app, choose <strong>Enter invitation code</strong>, and type it in.
            </p>
            <p style="margin:0 0 24px;font-size:16px;line-height:24px;color:{Ink};">
              This code works once and expires on <strong>{Encode(expiry)}</strong>.
            </p>

            <p style="margin:0;font-size:14px;line-height:20px;color:{Muted};">
              If you were not expecting this invitation, you can ignore this email — no account is
              created until the code is used.
            </p>
            """;

        var text = $"""
            {opening}

            Your invitation code: {code}

            Open the Beyond Movement app, choose "Enter invitation code", and type it in.
            This code works once and expires on {expiry}.

            If you were not expecting this invitation, you can ignore this email - no account is
            created until the code is used.
            """;

        return new EmailMessage(to, subject, WrapHtml(body, logoUrl), WrapText(text));
    }

    public static EmailMessage PasswordReset(string to, string link, int lifetimeHours, string? logoUrl = null)
    {
        const string subject = "Reset your Beyond Movement password";

        var hours = lifetimeHours == 1 ? "1 hour" : $"{lifetimeHours} hours";

        var body = $"""
            <p style="margin:0 0 24px;font-size:16px;line-height:24px;color:{Ink};">
              We received a request to reset your Beyond Movement password.
            </p>

            <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:0 0 24px;">
              <tr>
                <td style="background:{Brand};border-radius:8px;">
                  <a href="{Encode(link)}"
                     style="display:inline-block;padding:14px 32px;font-size:16px;font-weight:600;
                            color:#FFFFFF;text-decoration:none;">Set a new password</a>
                </td>
              </tr>
            </table>

            <p style="margin:0 0 24px;font-size:16px;line-height:24px;color:{Ink};">
              This link can be used once and expires in <strong>{hours}</strong>.
            </p>

            <p style="margin:0 0 8px;font-size:14px;line-height:20px;color:{Muted};">
              If the button does not open the app, copy this address into your phone's browser:
            </p>
            <p style="margin:0 0 24px;font-size:13px;line-height:20px;color:{Muted};word-break:break-all;">
              {Encode(link)}
            </p>

            <p style="margin:0;font-size:14px;line-height:20px;color:{Muted};">
              <strong>If you did not request this, ignore this email.</strong> Your password will not
              change, and nobody can use this link without access to this inbox.
            </p>
            """;

        var text = $"""
            We received a request to reset your Beyond Movement password.

            Open this link on your phone to set a new one:
            {link}

            The link can be used once and expires in {hours}.

            If you did not request this, ignore this email. Your password will not change, and
            nobody can use this link without access to this inbox.
            """;

        return new EmailMessage(to, subject, WrapHtml(body, logoUrl), WrapText(text));
    }

    /// <summary>
    /// The masthead. An image when a hosted logo is configured, the wordmark set as type
    /// otherwise — and the type version is also what shows when a recipient has images turned
    /// off, which is the default in several clients.
    /// </summary>
    private static string Masthead(string? logoUrl) =>
        string.IsNullOrWhiteSpace(logoUrl)
            ? $"""
               <div style="font-family:Helvetica,Arial,sans-serif;font-size:22px;font-weight:700;
                           letter-spacing:1px;color:{Brand};">Beyond Movement</div>
               <div style="font-family:Helvetica,Arial,sans-serif;font-size:10px;
                           letter-spacing:3px;color:{Brand};opacity:0.75;margin-top:6px;">
                 MENTAL PERFORMANCE
               </div>
               """
            : $"""
               <img src="{Encode(logoUrl)}" alt="Beyond Movement" width="132" height="132"
                    style="display:block;border:0;outline:none;text-decoration:none;
                           width:132px;height:132px;max-width:132px;">
               """;

    /// <summary>Shared HTML frame: masthead, content well, footer.</summary>
    private static string WrapHtml(string content, string? logoUrl = null) =>
        $"""
           <!DOCTYPE html>
           <html lang="en">
           <head>
             <meta charset="utf-8">
             <meta name="viewport" content="width=device-width,initial-scale=1">
             <title>Beyond Movement</title>
           </head>
           <body style="margin:0;padding:0;background:{Canvas};">
             <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"
                    style="background:{Canvas};padding:32px 16px;">
               <tr>
                 <td align="center">
                   <table role="presentation" width="600" cellpadding="0" cellspacing="0" border="0"
                          style="max-width:600px;width:100%;background:#FFFFFF;border-radius:12px;
                                 border:1px solid {Line};">
                     <tr>
                       <td style="padding:28px 40px;background:{BrandTint};border-radius:12px 12px 0 0;
                                  border-bottom:1px solid {Line};" align="center">
           {Masthead(logoUrl)}
                       </td>
                     </tr>
                     <tr>
                       <td style="padding:32px 40px;font-family:Helvetica,Arial,sans-serif;">
           {content}
                       </td>
                     </tr>
                     <tr>
                       <td style="padding:20px 40px 28px;border-top:1px solid {Line};
                                  font-family:Helvetica,Arial,sans-serif;font-size:12px;
                                  line-height:18px;color:{Muted};">
                         Sent by Beyond Movement. Please do not reply to this message.
                       </td>
                     </tr>
                   </table>
                 </td>
               </tr>
             </table>
           </body>
           </html>
           """;

    /// <summary>Shared plain-text frame, for clients that refuse HTML.</summary>
    private static string WrapText(string content) =>
        $"""
         BEYOND MOVEMENT - Mental Performance

         {content}

         --
         Sent by Beyond Movement. Please do not reply to this message.
         """;

    // Codes and links are generated server-side, but encoding is not conditional on trust.
    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

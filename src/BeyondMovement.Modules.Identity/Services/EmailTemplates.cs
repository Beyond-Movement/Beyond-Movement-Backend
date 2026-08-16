using System.Net;

namespace BeyondMovement.Modules.Identity.Services;

/// <summary>
/// The wording and markup of every email this module sends, in one place so copy can be
/// reviewed without reading handler code.
/// <para>
/// Email HTML is not web HTML. Layout is table-based with inline styles because Outlook
/// renders through Word and ignores most of a stylesheet; widths are fixed and repeated as
/// attributes; <c>mso-line-height-rule</c> appears wherever line height matters. The only
/// stylesheet is a small media query, which clients that ignore it simply fall back from.
/// </para>
/// </summary>
public static class EmailTemplates
{
    // Read off the logo: royal blue wordmark on a pale yellow disc.
    private const string Brand = "#3b4cb8";
    private const string BrandTint = "#f7f2ad";
    private const string Ink = "#201e1d";
    private const string Body = "#4a4745";
    private const string Muted = "#6b6764";
    private const string Rule = "#cfcdc9";
    private const string Surface = "#f3f2f2";
    private const string Canvas = "#e8e7e4";
    private const string FooterInk = "#dcdff6";

    private const int LogoPixels = 104;

    public static EmailMessage Invitation(
        string to,
        string code,
        DateTime expiresAtUtc,
        bool isResend = false,
        EmailBranding? branding = null)
    {
        branding ??= EmailBranding.None;

        var subject = isResend
            ? "Your new Beyond Movement invitation code"
            : "You're invited to Beyond Movement";

        var opening = isResend
            ? "Here is your new code. Any code you received earlier no longer works."
            : "Focus, pressure, recovery, confidence &mdash; the work that happens between sessions. Your seat is reserved with the code below.";

        var expiry = expiresAtUtc.ToString("d MMMM yyyy");

        var preheader = isResend
            ? "Your new Beyond Movement invitation code is inside."
            : "Your coach has invited you to Beyond Movement. One code, one tap, and your mental training starts.";

        var content = $$"""
              <tr>
                <td class="px" style="padding:44px 40px 0 40px;">
                  <h1 class="h1" style="margin:0;font-family:Arial,Helvetica,sans-serif;font-size:38px;line-height:42px;mso-line-height-rule:exactly;letter-spacing:-1px;color:{{Ink}};font-weight:bold;">You're invited.</h1>
                  <p style="margin:20px 0 0 0;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:26px;mso-line-height-rule:exactly;color:{{Body}};">{{opening}}</p>
                </td>
              </tr>

              <tr>
                <td class="px" style="padding:32px 40px 0 40px;">
                  <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="width:100%;">
                    <tr>
                      <td style="background-color:#ffffff;border:2px solid {{Brand}};padding:22px 24px;">
                        <p style="margin:0 0 8px 0;font-family:Arial,Helvetica,sans-serif;font-size:11px;line-height:14px;mso-line-height-rule:exactly;letter-spacing:3px;text-transform:uppercase;color:{{Brand}};font-weight:bold;">Invitation code</p>
                        <p class="code" style="margin:0;font-family:'Courier New',Courier,monospace;font-size:32px;line-height:36px;mso-line-height-rule:exactly;letter-spacing:6px;color:{{Brand}};font-weight:bold;">{{Encode(code)}}</p>
                      </td>
                    </tr>
                  </table>
                  <p style="margin:12px 0 0 0;font-family:Arial,Helvetica,sans-serif;font-size:13px;line-height:20px;mso-line-height-rule:exactly;color:{{Muted}};">Works once &middot; Expires <strong style="color:{{Ink}};">{{Encode(expiry)}}</strong></p>
                </td>
              </tr>

              <tr>
                <td class="px" style="padding:44px 40px 0 40px;">
                  <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="width:100%;border-top:2px solid {{Ink}};">
                    {{Step("01", "Open the Beyond Movement app on your phone.")}}
                    {{Divider()}}
                    {{Step("02", "Choose <strong>Enter invitation code</strong>.")}}
                    {{Divider()}}
                    {{Step("03", "Type the code above and finish setting up your profile.")}}
                    <tr><td colspan="2" style="border-top:2px solid {{Ink}};font-size:0;line-height:0;">&nbsp;</td></tr>
                  </table>
                </td>
              </tr>

              <tr>
                <td class="px" style="padding:36px 40px 40px 40px;">
                  <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="width:100%;">
                    <tr>
                      <td style="background-color:{{Canvas}};padding:18px 20px;font-family:Arial,Helvetica,sans-serif;font-size:13px;line-height:20px;mso-line-height-rule:exactly;color:{{Body}};">Not expecting this? Ignore the email &mdash; no account exists until the code is used.</td>
                    </tr>
                  </table>
                </td>
              </tr>
            """;

        var text = $"""
            {(isResend
                ? "Here is your new code. Any code you received earlier no longer works."
                : "Your coach has invited you to join Beyond Movement.")}

            Your invitation code: {code}

            Works once. Expires {expiry}.

            1. Open the Beyond Movement app on your phone.
            2. Choose "Enter invitation code".
            3. Type the code above and finish setting up your profile.

            Not expecting this? Ignore this email - no account exists until the code is used.
            """;

        return new EmailMessage(to, subject, Wrap(preheader, content, branding), WrapText(text, branding));
    }

    public static EmailMessage PasswordReset(
        string to, string link, int lifetimeHours, EmailBranding? branding = null)
    {
        branding ??= EmailBranding.None;

        const string subject = "Reset your Beyond Movement password";
        var hours = lifetimeHours == 1 ? "1 hour" : $"{lifetimeHours} hours";

        var content = $$"""
              <tr>
                <td class="px" style="padding:44px 40px 0 40px;">
                  <h1 class="h1" style="margin:0;font-family:Arial,Helvetica,sans-serif;font-size:38px;line-height:42px;mso-line-height-rule:exactly;letter-spacing:-1px;color:{{Ink}};font-weight:bold;">Reset your password.</h1>
                  <p style="margin:20px 0 0 0;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:26px;mso-line-height-rule:exactly;color:{{Body}};">We received a request to set a new password on your Beyond Movement account.</p>
                </td>
              </tr>

            {{ActionButton(link, "Set a new password")}}

              <tr>
                <td class="px" style="padding:28px 40px 0 40px;">
                  <p style="margin:0;font-family:Arial,Helvetica,sans-serif;font-size:13px;line-height:20px;mso-line-height-rule:exactly;color:{{Muted}};">Works once &middot; Expires in <strong style="color:{{Ink}};">{{hours}}</strong></p>
                  <p style="margin:16px 0 0 0;font-family:Arial,Helvetica,sans-serif;font-size:13px;line-height:20px;mso-line-height-rule:exactly;color:{{Muted}};">If the button does not open the app, copy this address into your phone's browser:</p>
                  <p style="margin:6px 0 0 0;font-family:'Courier New',Courier,monospace;font-size:12px;line-height:20px;mso-line-height-rule:exactly;color:{{Muted}};word-break:break-all;">{{Encode(link)}}</p>
                </td>
              </tr>

              <tr>
                <td class="px" style="padding:36px 40px 40px 40px;">
                  <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="width:100%;">
                    <tr>
                      <td style="background-color:{{Canvas}};padding:18px 20px;font-family:Arial,Helvetica,sans-serif;font-size:13px;line-height:20px;mso-line-height-rule:exactly;color:{{Body}};"><strong style="color:{{Ink}};">If you did not request this, ignore this email.</strong> Your password will not change, and nobody can use the link without access to this inbox.</td>
                    </tr>
                  </table>
                </td>
              </tr>
            """;

        var text = $"""
            We received a request to set a new password on your Beyond Movement account.

            Open this link on your phone:
            {link}

            Works once. Expires in {hours}.

            If you did not request this, ignore this email. Your password will not change, and
            nobody can use the link without access to this inbox.
            """;

        return new EmailMessage(to, subject, Wrap("Set a new Beyond Movement password.", content, branding), WrapText(text, branding));
    }

    // ---------------------------------------------------------------- pieces

    /// <summary>
    /// The masthead. An image when a hosted logo is configured, the wordmark set as type
    /// otherwise &mdash; which is also what shows when a recipient blocks images, the default
    /// in several clients.
    /// </summary>
    private static string Masthead(string? logoUrl) =>
        string.IsNullOrWhiteSpace(logoUrl)
            ? $"""
                         <td valign="middle" style="font-family:Arial,Helvetica,sans-serif;font-size:26px;line-height:30px;mso-line-height-rule:exactly;letter-spacing:1px;color:{BrandTint};font-weight:bold;">Beyond Movement</td>
               """
            : $"""
                         <td width="{LogoPixels}" valign="middle" style="width:{LogoPixels}px;padding:0 20px 0 0;">
                           <img src="{Encode(logoUrl)}" width="{LogoPixels}" height="{LogoPixels}" alt="Beyond Movement" style="display:block;border:0;outline:none;text-decoration:none;width:{LogoPixels}px;height:{LogoPixels}px;">
                         </td>
                         <td valign="middle" style="font-family:Arial,Helvetica,sans-serif;font-size:20px;line-height:20px;mso-line-height-rule:exactly;letter-spacing:3px;text-transform:uppercase;color:{BrandTint};font-weight:bold;">Mental performance<br>coaching</td>
               """;

    /// <summary>
    /// Used only by the password reset, where the link is the entire purpose of the message.
    /// The invitation deliberately has no button: the code is what the athlete needs, and a
    /// button pointing at a domain that does not resolve reads as broken, or as phishing.
    /// </summary>
    private static string ActionButton(string url, string label) =>
        $"""
                 <tr>
                   <td class="px" style="padding:28px 40px 0 40px;">
                     <table role="presentation" cellpadding="0" cellspacing="0" border="0">
                       <tr>
                         <td bgcolor="{Brand}" style="background-color:{Brand};padding:16px 28px;">
                           <a href="{Encode(url)}" style="display:block;white-space:nowrap;font-family:Arial,Helvetica,sans-serif;font-size:15px;line-height:18px;mso-line-height-rule:exactly;letter-spacing:1px;text-transform:uppercase;font-weight:bold;color:{BrandTint};text-decoration:none;">{label} &rarr;</a>
                         </td>
                       </tr>
                     </table>
                   </td>
                 </tr>
               """;

    private static string Step(string number, string text) =>
        $"""
             <tr>
               <td width="44" valign="top" style="width:44px;padding:18px 0;font-family:Arial,Helvetica,sans-serif;font-size:13px;line-height:20px;mso-line-height-rule:exactly;color:{Brand};font-weight:bold;">{number}</td>
               <td valign="top" style="padding:18px 0;font-family:Arial,Helvetica,sans-serif;font-size:15px;line-height:22px;mso-line-height-rule:exactly;color:{Ink};">{text}</td>
             </tr>
         """;

    private static string Divider() =>
        $"""<tr><td colspan="2" style="border-top:1px solid {Rule};font-size:0;line-height:0;">&nbsp;</td></tr>""";

    private static string Footer(EmailBranding branding)
    {
        var address = string.IsNullOrWhiteSpace(branding.PostalAddress)
            ? string.Empty
            : $"<br>{Encode(branding.PostalAddress)}";

        return $"""
                  <tr>
                    <td class="px" style="background-color:{Brand};padding:28px 40px;">
                      <p style="margin:0 0 6px 0;font-family:Arial,Helvetica,sans-serif;font-size:12px;line-height:20px;mso-line-height-rule:exactly;letter-spacing:2px;text-transform:uppercase;color:{BrandTint};font-weight:bold;">Beyond Movement</p>
                      <p style="margin:0;font-family:Arial,Helvetica,sans-serif;font-size:12px;line-height:20px;mso-line-height-rule:exactly;color:{FooterInk};">Sent by Beyond Movement. Please do not reply to this message.{address}</p>
                    </td>
                  </tr>
                """;
    }

    /// <summary>The shared frame: preheader, masthead, content, footer.</summary>
    private static string Wrap(string preheader, string content, EmailBranding branding) =>
        $$"""
          <!DOCTYPE html>
          <html lang="en">
          <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta name="color-scheme" content="light dark">
          <meta name="supported-color-schemes" content="light dark">
          <title>Beyond Movement</title>
          <!--[if mso]>
          <style>body,table,td,a{font-family:Arial,Helvetica,sans-serif !important;}</style>
          <![endif]-->
          <style>
            @media only screen and (max-width:620px){
              .px{padding-left:24px !important;padding-right:24px !important;}
              .h1{font-size:30px !important;line-height:34px !important;}
              .code{font-size:26px !important;letter-spacing:4px !important;}
            }
          </style>
          </head>
          <body style="margin:0;padding:0;background-color:{{Canvas}};">
          <span style="display:none;font-size:1px;color:{{Canvas}};line-height:1px;max-height:0;max-width:0;opacity:0;overflow:hidden;">{{Encode(preheader)}}</span>

          <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="background-color:{{Canvas}};">
          <tr><td align="center" style="padding:32px 12px;">

          <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="600" style="width:600px;max-width:600px;background-color:{{Surface}};">

            <tr>
              <td class="px" style="background-color:{{Brand}};padding:36px 40px 32px 40px;">
                <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="width:100%;">
                  <tr>
          {{Masthead(branding.LogoUrl)}}
                  </tr>
                </table>
              </td>
            </tr>

          {{content}}

          {{Footer(branding)}}

          </table>

          </td></tr>
          </table>

          </body>
          </html>
          """;

    private static string WrapText(string content, EmailBranding branding)
    {
        var address = string.IsNullOrWhiteSpace(branding.PostalAddress)
            ? string.Empty
            : $"\n{branding.PostalAddress}";

        return $"""
            BEYOND MOVEMENT - Mental performance coaching

            {content}

            --
            Sent by Beyond Movement. Please do not reply to this message.{address}
            """;
    }

    // Codes and links are generated server-side, but encoding is not conditional on trust.
    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

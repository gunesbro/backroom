using System.Text;
using Backroom.Modules.Email.Application.Deliveries;
using Backroom.Modules.Email.Application.Sending;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.Email.Application.Templates;

/// <summary>
/// The invitation email (docs/contracts/phase-1.md, "Invitation security", Spam and phishing): sender name and subject
/// from fixed texts with the organization's name (cleaned, length-limited, no line breaks), a plain-text and an HTML
/// part, the invitation link as the only link (to the web origin), and what to do if the invitation was unexpected.
/// No inviter name. Untrusted names are HTML-encoded and kept from becoming links.
/// </summary>
internal static class InvitationEmail
{
    /// <summary>Bump the version when the texts or the layout change; the delivery log records it.</summary>
    public static readonly EmailTemplateId Template = new("invitation", 1);

    public static EmailMessage Create(InvitationLink invitation, string? locale)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        var texts = InvitationEmailTexts.For(locale);
        var name = EmailText.CleanName(invitation.OrganizationName, texts.UnnamedOrganization);
        var bodyName = EmailText.WithoutLinks(name);
        var link = invitation.Url.AbsoluteUri;

        var intro = EmailText.Fill(texts.Intro, bodyName);
        var limits = EmailText.Fill(texts.Limits, bodyName);
        var unexpected = EmailText.Fill(texts.Unexpected, bodyName);

        // Headers: no '@' (a name must not pass for an address) and no links. Without a visible name the sender name
        // has its own text, because the fallback reads well only inside a sentence.
        var headerName = EmailText.HeaderName(invitation.OrganizationName, string.Empty);
        var subject = EmailText.Fill(texts.Subject, headerName.Length == 0 ? texts.UnnamedOrganization : headerName);
        var senderName = headerName.Length == 0 ? EmailText.Fill(texts.UnnamedSenderName, string.Empty) : EmailText.Fill(texts.SenderName, headerName);

        var text = new StringBuilder(1024)
            .Append(intro).Append("\n\n")
            .Append(EmailText.Fill(texts.LinkHint, bodyName)).Append('\n')
            .Append(link).Append("\n\n")
            .Append(limits).Append("\n\n")
            .Append(unexpected).Append('\n')
            .ToString();

        return EmailMessage.Create(
            invitation.Email,
            subject,
            text,
            Html(texts, subject, intro, link, limits, unexpected, bodyName),
            senderName);
    }

    // Inline styles only (mail clients drop style sheets). Every text is encoded; the link is the only href.
    private static string Html(InvitationEmailTexts texts, string subject, string intro, string link, string limits, string unexpected, string bodyName)
    {
        var href = EmailText.Html(link);
        return new StringBuilder(2048)
            .Append("<!DOCTYPE html>\n<html lang=\"").Append(texts.Language).Append("\">\n<head>\n")
            .Append("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(EmailText.Html(subject)).Append("</title>\n</head>\n")
            .Append("<body style=\"margin:0;padding:0;background-color:#f4f4f5;\">\n")
            .Append("<div style=\"max-width:560px;margin:0 auto;padding:32px 24px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:1.5;color:#18181b;background-color:#ffffff;\">\n")
            .Append("<p style=\"margin:0 0 24px;\">").Append(EmailText.Html(intro)).Append("</p>\n")
            .Append("<p style=\"margin:0 0 24px;\"><a href=\"").Append(href)
            .Append("\" style=\"display:inline-block;padding:12px 20px;background-color:#1d4ed8;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold;\">")
            .Append(EmailText.Html(EmailText.Fill(texts.Button, bodyName))).Append("</a></p>\n")
            .Append("<p style=\"margin:0 0 8px;font-size:14px;color:#52525b;\">").Append(EmailText.Html(EmailText.Fill(texts.CopyHint, bodyName))).Append("</p>\n")
            .Append("<p style=\"margin:0 0 24px;font-size:14px;color:#52525b;word-break:break-all;\">").Append(href).Append("</p>\n")
            .Append("<p style=\"margin:0 0 16px;font-size:14px;color:#52525b;\">").Append(EmailText.Html(limits)).Append("</p>\n")
            .Append("<p style=\"margin:0;font-size:14px;color:#52525b;\">").Append(EmailText.Html(unexpected)).Append("</p>\n")
            .Append("</div>\n</body>\n</html>\n")
            .ToString();
    }
}

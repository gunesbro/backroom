using Backroom.BuildingBlocks.Domain.Email;

namespace Backroom.Modules.Email.Application.Sending;

/// <summary>
/// One message to one recipient, built from a fixed template. The sender and its name come from configuration,
/// never from the caller. CR and LF are stripped from the subject so no header can be injected through it.
/// Its content is never logged or stored.
/// </summary>
internal sealed class EmailMessage
{
    public const int MaxSubjectLength = 200;

    /// <summary>Each body; invitation-sized messages are a few kilobytes.</summary>
    public const int MaxBodyLength = 100_000;

    public const int MaxSenderNameLength = 100;

    private EmailMessage(string to, string? senderName, string subject, string textBody, string? htmlBody)
    {
        To = to;
        SenderName = senderName;
        Subject = subject;
        TextBody = textBody;
        HtmlBody = htmlBody;
    }

    /// <summary>The recipient, normalized (<see cref="EmailAddress"/>).</summary>
    public string To { get; }

    /// <summary>The sender's display name from the template (the configured one if <see langword="null"/>); the address is always the configured one.</summary>
    public string? SenderName { get; }

    public string Subject { get; }

    public string TextBody { get; }

    /// <summary>An HTML alternative to <see cref="TextBody"/>, if any; its untrusted values are already encoded.</summary>
    public string? HtmlBody { get; }

    /// <summary>Throws <see cref="ArgumentException"/> for an invalid recipient, an empty subject or body, or a value over its limit.</summary>
    public static EmailMessage Create(string to, string subject, string textBody, string? htmlBody = null, string? senderName = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(textBody);
        if (!EmailAddress.TryNormalize(to, out var recipient))
        {
            // The address itself is not put in the message: exception messages end up in logs.
            throw new ArgumentException("The recipient is not one valid email address.", nameof(to));
        }

        var oneLineSubject = StripLineBreaks(subject).Trim();
        if (oneLineSubject.Length is 0 or > MaxSubjectLength)
        {
            throw new ArgumentException($"The subject must have 1 to {MaxSubjectLength} characters.", nameof(subject));
        }

        if (textBody.Length is 0 or > MaxBodyLength)
        {
            throw new ArgumentException($"The text body must have 1 to {MaxBodyLength} characters.", nameof(textBody));
        }

        if (htmlBody is { Length: 0 or > MaxBodyLength })
        {
            throw new ArgumentException($"The HTML body must have 1 to {MaxBodyLength} characters.", nameof(htmlBody));
        }

        string? oneLineSenderName = null;
        if (senderName is not null)
        {
            oneLineSenderName = StripLineBreaks(senderName).Trim();
            if (oneLineSenderName.Length is 0 or > MaxSenderNameLength)
            {
                throw new ArgumentException($"The sender name must have 1 to {MaxSenderNameLength} characters.", nameof(senderName));
            }
        }

        return new EmailMessage(recipient, oneLineSenderName, oneLineSubject, textBody, htmlBody);
    }

    /// <summary>Removes CR, LF and the other line and paragraph separators, so a value stays on one header line.</summary>
    public static string StripLineBreaks(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Create(value.Length - CountLineBreaks(value), value, static (span, source) =>
        {
            var index = 0;
            foreach (var character in source)
            {
                if (!IsLineBreak(character))
                {
                    span[index++] = character;
                }
            }
        });
    }

    private static int CountLineBreaks(string value)
    {
        var count = 0;
        foreach (var character in value)
        {
            if (IsLineBreak(character))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsLineBreak(char character) =>
        character is (char)0x0A or (char)0x0B or (char)0x0C or (char)0x0D or (char)0x85 or (char)0x2028 or (char)0x2029;
}

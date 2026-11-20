using System.Globalization;
using System.Text;

namespace Backroom.BuildingBlocks.Domain.Email;

/// <summary>
/// An email address as the platform compares it (docs/contracts/phase-1.md, "Cross-cutting rules"): exactly one
/// address, without display name, comments or quoting, with an ASCII local part, normalized by trim, Unicode NFC
/// and <c>ToLowerInvariant</c> (never culture-sensitive, because of Turkish İ/ı), its domain converted to punycode.
/// Nothing provider-specific is rewritten. The cell normalizes invited addresses with it, and the control plane
/// both the invited address and the accepting account's verified email, so the two compare equal.
/// </summary>
public static class EmailAddress
{
    public const int MaxLength = 254;

    private const int MaxLocalPartLength = 64;
    private const int MaxDomainLength = 253;
    private const int MaxLabelLength = 63;

    /// <summary>The characters RFC 5322 allows in an unquoted local part besides letters, digits and dots.</summary>
    private const string LocalPartSymbols = "!#$%&'*+/=?^_`{|}~-";

    private static readonly IdnMapping s_idn = new() { UseStd3AsciiRules = true, AllowUnassigned = false };

    public static bool TryNormalize(string? input, out string email)
    {
        email = string.Empty;
        if (input is null || input.Length > MaxLength * 2)
        {
            return false;
        }

        string text;
        try
        {
            text = input.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Not valid Unicode (a lone surrogate).
            return false;
        }

        var at = text.LastIndexOf('@');
        if (text.Length > MaxLength || at <= 0 || at == text.Length - 1 || !IsLocalPart(text.AsSpan(0, at)))
        {
            return false;
        }

        string domain;
        try
        {
            domain = s_idn.GetAscii(text, at + 1).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!IsDomain(domain) || at + 1 + domain.Length > MaxLength)
        {
            return false;
        }

        email = string.Concat(text.AsSpan(0, at + 1), domain);
        return true;
    }

    // ASCII letters and digits, the RFC's symbols, and single dots inside. This excludes whitespace, control
    // characters (CR/LF), commas, quotes, angle brackets and a second '@'. Non-ASCII local parts are refused in
    // Phase 1: many mail servers cannot deliver them (SMTPUTF8), look-alikes such as fullwidth letters would show
    // as someone else's address, and Turkish İ/ı would not compare like their ASCII forms.
    private static bool IsLocalPart(ReadOnlySpan<char> local)
    {
        if (local.Length > MaxLocalPartLength || local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in local)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '.' && !LocalPartSymbols.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // A host name of at least two labels whose last one is not a number: no single-label hosts, no IP addresses.
    private static bool IsDomain(string domain)
    {
        if (domain.Length > MaxDomainLength || !domain.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        var lastLabelIsNumeric = false;
        foreach (var range in domain.AsSpan().Split('.'))
        {
            var label = domain.AsSpan(range);
            if (label.IsEmpty || label.Length > MaxLabelLength || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            lastLabelIsNumeric = true;
            foreach (var character in label)
            {
                if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
                {
                    return false;
                }

                lastLabelIsNumeric &= char.IsAsciiDigit(character);
            }
        }

        return !lastLabelIsNumeric;
    }
}

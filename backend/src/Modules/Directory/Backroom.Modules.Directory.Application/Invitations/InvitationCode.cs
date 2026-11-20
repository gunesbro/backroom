using System.Security.Cryptography;
using System.Text;

namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// An invitation code as the invitee's client sends it (docs/contracts/phase-1.md, "Invitation security"): 43 base64url
/// characters of a 256-bit HMAC. The control plane only ever compares its SHA-256 with the hash the cell registered.
/// </summary>
internal static class InvitationCode
{
    public const int Length = 43;

    /// <summary>The code's SHA-256 over its characters, or <see langword="null"/> if it is not a well-formed code.</summary>
    public static byte[]? TryHash(string? code)
    {
        if (code is not { Length: Length } || !code.All(IsBase64UrlCharacter))
        {
            return null;
        }

        Span<byte> characters = stackalloc byte[Length];
        Encoding.ASCII.GetBytes(code, characters);
        return SHA256.HashData(characters);
    }

    private static bool IsBase64UrlCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
}

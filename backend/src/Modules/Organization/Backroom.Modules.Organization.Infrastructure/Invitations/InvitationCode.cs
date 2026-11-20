using System.Buffers.Text;
using System.Security.Cryptography;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>
/// The invitation code (docs/contracts/phase-1.md, "Invitation security"):
/// <c>base64url(HMAC-SHA256(key, invitation_id ‖ salt))</c> without padding, where the id is its 16 bytes in
/// network order (as the UUID is written) and the salt 16 random bytes. The hash stored by the cell and the
/// control plane is SHA-256 over the code's characters, which is what a client sends.
/// </summary>
internal static class InvitationCode
{
    public const int SaltLength = 16;

    /// <summary>43 base64url characters carry the 32 bytes of the MAC.</summary>
    public const int Length = 43;

    private const int IdLength = 16;

    /// <summary>Writes the code's characters (ASCII) into <paramref name="code"/>, which the caller clears after use.</summary>
    public static void Derive(ReadOnlySpan<byte> key, Guid invitationId, ReadOnlySpan<byte> salt, Span<byte> code)
    {
        if (salt.Length != SaltLength || code.Length != Length)
        {
            throw new ArgumentException("The salt or the code buffer has the wrong length.");
        }

        Span<byte> message = stackalloc byte[IdLength + SaltLength];
        invitationId.TryWriteBytes(message, bigEndian: true, out _);
        salt.CopyTo(message[IdLength..]);

        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, message, mac);
        Base64Url.EncodeToUtf8(mac, code);
        CryptographicOperations.ZeroMemory(mac);
        CryptographicOperations.ZeroMemory(message);
    }

    public static byte[] Hash(ReadOnlySpan<byte> code) => SHA256.HashData(code);
}

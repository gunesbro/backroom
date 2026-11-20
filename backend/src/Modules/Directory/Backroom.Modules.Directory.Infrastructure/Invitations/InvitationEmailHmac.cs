using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Domain.Email;
using Backroom.Modules.Directory.Application.Invitations;

namespace Backroom.Modules.Directory.Infrastructure.Invitations;

/// <summary>HMAC-SHA256 of an invitation's id and normalized email with the control plane's key. Error messages never quote the key.</summary>
internal sealed class InvitationEmailHmac : IInvitationEmailHmac
{
    private const int IdBytes = 16;

    private readonly byte[] _key;

    public InvitationEmailHmac(IOptions<InvitationEmailHmacOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TryDecode(options.Value.EmailHmacKey, out var key, out var error))
        {
            throw new OptionsValidationException(InvitationEmailHmacOptions.SectionName, typeof(InvitationEmailHmacOptions), [error]);
        }

        _key = key;
    }

    public byte[]? TryCompute(Guid invitationId, string? email)
    {
        if (!EmailAddress.TryNormalize(email, out var normalized))
        {
            return null;
        }

        // The invitation's id (16 bytes, network order) comes first, so the same address gives a different HMAC in
        // every invitation and rows cannot be linked to one person without the key.
        var addressBytes = Encoding.ASCII.GetByteCount(normalized);
        var message = new byte[IdBytes + addressBytes];
        invitationId.TryWriteBytes(message, bigEndian: true, out _);
        Encoding.ASCII.GetBytes(normalized, message.AsSpan(IdBytes));
        return HMACSHA256.HashData(_key, message);
    }

    /// <summary>Decodes and checks the key; <paramref name="error"/> says what is wrong without quoting it.</summary>
    public static bool TryDecode(string? value, out byte[] key, out string error)
    {
        var buffer = new byte[(value?.Length ?? 0) * 3 / 4 + 3];
        var decoded = Convert.TryFromBase64String(value ?? string.Empty, buffer, out var length);
        key = buffer[..length];
        CryptographicOperations.ZeroMemory(buffer);
        if (!decoded || length < InvitationEmailHmacOptions.MinKeyBytes)
        {
            error = $"{InvitationEmailHmacOptions.SectionName}:EmailHmacKey must be base64 of at least {InvitationEmailHmacOptions.MinKeyBytes} bytes.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

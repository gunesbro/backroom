using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Backroom.Modules.Email.Application.Deliveries;
using Backroom.Modules.Email.Infrastructure.Sending;

namespace Backroom.Modules.Email.Infrastructure.Deliveries;

/// <summary>HMAC-SHA256 of the normalized address with the cell's recipient hash key (<see cref="EmailOptions.RecipientHashKey"/>).</summary>
internal sealed class RecipientHasher : IRecipientHasher
{
    private readonly byte[] _key;

    public RecipientHasher(IOptions<EmailOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TryReadKey(options.Value.RecipientHashKey, out var key))
        {
            throw new InvalidOperationException("The recipient hash key is missing or too short.");
        }

        _key = key;
    }

    public byte[] Hash(string normalizedEmail)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        return HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalizedEmail));
    }

    public static bool TryReadKey(string? value, [NotNullWhen(true)] out byte[]? key)
    {
        key = null;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var buffer = new byte[(value.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64String(value, buffer, out var written) || written < EmailOptions.MinRecipientHashKeyBytes)
        {
            return false;
        }

        key = buffer[..written];
        return true;
    }
}

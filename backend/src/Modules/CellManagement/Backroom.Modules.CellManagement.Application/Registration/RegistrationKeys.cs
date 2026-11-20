using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Backroom.Modules.CellManagement.Application.Registration;

/// <summary>One-time registration keys: 256 random bits, valid 24 hours, stored only as SHA-256.</summary>
internal static partial class RegistrationKeys
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>A new key, base64url.</summary>
    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The stored form. A plain hash is enough: the key has full entropy.</summary>
    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>
    /// Whether <paramref name="key"/> has the shape of a key (43 base64url characters), checked before any
    /// database work. Only the alphabet and length matter: keys are compared by hash, never decoded, so
    /// the AppHost's generated keys (any 43 letters and digits) are valid too.
    /// </summary>
    public static bool HasValidShape(string? key) => key is not null && KeyPattern().IsMatch(key);

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

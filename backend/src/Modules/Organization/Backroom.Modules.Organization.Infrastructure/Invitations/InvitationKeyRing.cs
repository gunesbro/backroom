using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>The configured invitation keys, decoded once. Error messages name versions, never key material.</summary>
internal sealed class InvitationKeyRing
{
    private readonly FrozenDictionary<int, byte[]> _keys;

    public InvitationKeyRing(IOptions<InvitationKeyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TryRead(options.Value, out var keys, out var error))
        {
            throw new OptionsValidationException(InvitationKeyOptions.SectionName, typeof(InvitationKeyOptions), [error]);
        }

        _keys = keys.ToFrozenDictionary();
        CurrentVersion = options.Value.CurrentVersion;
    }

    public int CurrentVersion { get; }

    public ReadOnlySpan<byte> CurrentKey => _keys[CurrentVersion];

    /// <summary>The key of <paramref name="version"/>, or an empty span if that version is not configured (any more).</summary>
    public ReadOnlySpan<byte> KeyOf(int version) => _keys.TryGetValue(version, out var key) ? key : default;

    /// <summary>Decodes and checks the settings; <paramref name="error"/> says what is wrong without quoting a key.</summary>
    public static bool TryRead(InvitationKeyOptions options, out Dictionary<int, byte[]> keys, out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        keys = [];
        if (options.Keys.Count is 0 or > InvitationKeyOptions.MaxVersions)
        {
            error = $"{InvitationKeyOptions.SectionName}:Keys must hold between 1 and {InvitationKeyOptions.MaxVersions} key versions.";
            return false;
        }

        foreach (var (name, value) in options.Keys)
        {
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
            {
                error = $"{InvitationKeyOptions.SectionName}:Keys has a version that is not a positive number.";
                return false;
            }

            var buffer = new byte[(value?.Length ?? 0) * 3 / 4 + 3];
            var decoded = Convert.TryFromBase64String(value ?? string.Empty, buffer, out var length);
            var key = buffer[..length];
            CryptographicOperations.ZeroMemory(buffer);
            if (!decoded || length < InvitationKeyOptions.MinKeyBytes)
            {
                error = $"{InvitationKeyOptions.SectionName}:Keys:{version} must be base64 of at least {InvitationKeyOptions.MinKeyBytes} bytes.";
                return false;
            }

            if (!keys.TryAdd(version, key))
            {
                error = $"{InvitationKeyOptions.SectionName}:Keys lists version {version} twice.";
                return false;
            }
        }

        if (!keys.ContainsKey(options.CurrentVersion))
        {
            error = $"{InvitationKeyOptions.SectionName}:CurrentVersion must name one of the configured key versions.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

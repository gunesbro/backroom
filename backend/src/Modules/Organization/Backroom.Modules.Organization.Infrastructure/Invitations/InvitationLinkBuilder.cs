using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Backroom.Modules.Organization.Application.Invitations;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>
/// Derives the code again from the stored salt and key version (<see cref="InvitationCode.Derive"/>), checks it
/// against the stored hash and puts it in the link's fragment, which browsers never send to a server. The code's
/// bytes are cleared; the link itself is a string, which the caller holds only while it sends the email.
/// </summary>
internal sealed class InvitationLinkBuilder(InvitationKeyRing keys, IOptions<InvitationLinkOptions> options) : IInvitationLinkBuilder
{
    /// <summary>The web app's invite page (docs/contracts/phase-1.md, "Invitation security", Transport).</summary>
    public const string InvitePath = "/invite#";

    // Read on first use: only the worker builds links, so only the worker needs (and validates) the web origin.
    private string? _origin;

    public Uri? Build(Guid invitationId, InvitationLinkSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var key = keys.KeyOf(source.KeyVersion);
        if (key.IsEmpty || source.Salt.Length != InvitationCode.SaltLength)
        {
            return null;
        }

        Span<byte> code = stackalloc byte[InvitationCode.Length];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            InvitationCode.Derive(key, invitationId, source.Salt, code);
            SHA256.HashData(code, hash);
            if (!CryptographicOperations.FixedTimeEquals(hash, source.CodeHash))
            {
                return null;
            }

            return new Uri(Origin() + InvitePath + Encoding.ASCII.GetString(code), UriKind.Absolute);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(code);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    // Scheme, host and a non-default port, without a trailing slash.
    private string Origin() =>
        _origin ??= (options.Value.WebOrigin ?? throw new InvalidOperationException($"{InvitationLinkOptions.SectionName}:WebOrigin is not configured."))
            .GetLeftPart(UriPartial.Authority);
}

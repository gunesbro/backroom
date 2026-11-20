using System.Security.Cryptography;
using Backroom.Modules.Organization.Application.Invitations;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>Issues codes under the current key version and keeps only their hash; the code is cleared from memory.</summary>
internal sealed class InvitationCodeIssuer(InvitationKeyRing keys) : IInvitationCodeIssuer
{
    public IssuedInvitationCode Issue(Guid invitationId)
    {
        var salt = RandomNumberGenerator.GetBytes(InvitationCode.SaltLength);
        Span<byte> code = stackalloc byte[InvitationCode.Length];
        InvitationCode.Derive(keys.CurrentKey, invitationId, salt, code);
        var hash = InvitationCode.Hash(code);
        CryptographicOperations.ZeroMemory(code);
        return new IssuedInvitationCode(salt, keys.CurrentVersion, hash);
    }
}

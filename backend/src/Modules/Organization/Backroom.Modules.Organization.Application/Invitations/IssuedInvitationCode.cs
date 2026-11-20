namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>What is stored of a newly issued code: its salt, the key version and <c>SHA-256(code)</c>, never the code.</summary>
internal sealed record IssuedInvitationCode(byte[] Salt, int KeyVersion, byte[] CodeHash);

using Microsoft.Extensions.Options;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>Stops the service at startup when the invitation keys are missing or weak.</summary>
internal sealed class InvitationKeyOptionsValidator : IValidateOptions<InvitationKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, InvitationKeyOptions options) =>
        InvitationKeyRing.TryRead(options, out _, out var error) ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
}

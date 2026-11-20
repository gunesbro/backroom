using Microsoft.Extensions.Options;

namespace Backroom.Modules.Directory.Infrastructure.Invitations;

/// <summary>Stops the control plane at startup when the email HMAC key is missing or weak.</summary>
internal sealed class InvitationEmailHmacOptionsValidator : IValidateOptions<InvitationEmailHmacOptions>
{
    public ValidateOptionsResult Validate(string? name, InvitationEmailHmacOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return InvitationEmailHmac.TryDecode(options.EmailHmacKey, out _, out var error)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>Stops the worker at startup unless invitation links point to one plain web origin (https outside Development).</summary>
internal sealed class InvitationLinkOptionsValidator(IHostEnvironment environment) : IValidateOptions<InvitationLinkOptions>
{
    public ValidateOptionsResult Validate(string? name, InvitationLinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TryCheck(options.WebOrigin, environment.IsDevelopment(), out var error)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }

    public static bool TryCheck(Uri? origin, bool isDevelopment, out string error)
    {
        const string Setting = InvitationLinkOptions.SectionName + ":WebOrigin";
        if (origin is null || !origin.IsAbsoluteUri || string.IsNullOrEmpty(origin.Host))
        {
            error = $"{Setting} must be the web app's absolute origin.";
            return false;
        }

        var https = origin.Scheme == Uri.UriSchemeHttps;
        var localHttp = isDevelopment && origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback;
        if (!https && !localHttp)
        {
            error = $"{Setting} must use https (http only in Development on a loopback host).";
            return false;
        }

        if (origin.UserInfo.Length > 0 || origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0)
        {
            error = $"{Setting} must be an origin only: no user information, path, query or fragment.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

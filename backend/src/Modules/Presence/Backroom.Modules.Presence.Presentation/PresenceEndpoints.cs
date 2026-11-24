using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.BuildingBlocks.Presentation.Organizations;
using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Presence.Presentation;

public static class PresenceEndpoints
{
    /// <summary>The feature flag of presence (the same name as the module's and the control plane's).</summary>
    public const string Flag = "Features:Presence";

    private static readonly Error s_invalid = Error.Validation(
        "presence.accounts_invalid", "accountIds must be 1 to 200 account ids, separated by commas.");

    /// <summary>200 ids of 36 characters with their commas.</summary>
    private const int MaxQueryLength = IPresenceReader.MaxAccounts * 37;

    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Off until presence is finished (CLAUDE.md: unfinished features ship behind a flag).
        if (endpoints.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>(Flag))
        {
            // A member reads people of the same organization only: the reader leaves everyone else out.
            endpoints.MapGet("/api/v1/orgs/{orgId:guid}/presence", ReadAsync).InOrganization();
        }

        return endpoints;
    }

    private static async Task<IResult> ReadAsync(string? accountIds, HttpResponse response, IPresenceReader reader, CancellationToken cancellationToken)
    {
        if (!TryParse(accountIds, out var ids))
        {
            return s_invalid.ToProblem();
        }

        // It changes all the time and is about people: never cached on the way.
        response.Headers.CacheControl = "no-store";
        var entries = await reader.ReadAsync(ids, cancellationToken);
        return TypedResults.Ok(new PresenceResponse([.. entries.Select(entry => new PersonPresenceResponse(
            entry.AccountId,
            entry.State switch
            {
                PresenceState.Online => "online",
                PresenceState.Idle => "idle",
                _ => "offline",
            },
            entry.DndUntil,
            entry.CustomStatus is { } custom ? new CustomStatusResponse(custom.Emoji, custom.Text, custom.ExpiresAt) : null))]));
    }

    private static bool TryParse(string? value, out List<Guid> ids)
    {
        ids = [];
        if (string.IsNullOrEmpty(value) || value.Length > MaxQueryLength)
        {
            return false;
        }

        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id) || id == Guid.Empty)
            {
                return false;
            }

            ids.Add(id);
        }

        ids = [.. ids.Distinct()];
        return ids.Count is > 0 and <= IPresenceReader.MaxAccounts;
    }
}

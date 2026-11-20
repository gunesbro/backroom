using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// The control plane acknowledged <paramref name="CodeVersion"/> of the invitation, so its link works and the email
/// can be sent (docs/contracts/phase-1.md, flow 3, step 3). Published once per code version. The consumer skips it if
/// a newer version exists, or if the invitation is no longer pending. <paramref name="Locale"/> is the language of the
/// email, always <see cref="DefaultLocale"/>: the cell must not learn whether the invited address has an account, so
/// not even that account's language is used. It carries neither the code nor the email.
/// </summary>
public sealed record InvitationReady(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid InvitationId,
    int CodeVersion,
    string Locale) : IIntegrationEvent
{
    /// <summary>The language of every invitation email (the product's fallback language).</summary>
    public const string DefaultLocale = "en-US";

    public static string EventType => "organization.invitation-ready.v1";
}

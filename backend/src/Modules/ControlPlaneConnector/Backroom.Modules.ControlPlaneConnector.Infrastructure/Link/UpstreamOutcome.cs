namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>An acknowledged invitation registration: which code version of which invitation (<see cref="UpstreamOutcomes"/>).</summary>
internal readonly record struct UpstreamOutcome(Guid OrganizationId, Guid InvitationId, int CodeVersion);

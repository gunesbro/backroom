namespace Backroom.Modules.Directory.Application.Organizations;

/// <summary>An organization's provisioning state: <c>provisioning</c>, <c>active</c> or <c>failed</c> with a code.</summary>
internal sealed record OrganizationState(Guid Id, string Status, string? FailureCode);

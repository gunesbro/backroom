namespace Backroom.Modules.Directory.Presentation;

/// <summary><c>provisioning</c>, <c>active</c> or <c>failed</c> with <see cref="Code"/>.</summary>
public sealed record OrganizationStateResponse(Guid Id, string Status, string? Code);

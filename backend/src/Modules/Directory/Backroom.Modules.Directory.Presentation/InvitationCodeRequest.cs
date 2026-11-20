namespace Backroom.Modules.Directory.Presentation;

/// <summary>An invitation code, only ever in a request body (never in a URL, so it stays out of logs).</summary>
public sealed record InvitationCodeRequest(string? Code);

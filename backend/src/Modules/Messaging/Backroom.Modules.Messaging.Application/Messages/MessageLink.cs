namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>A web address a message links to, as its link mark holds it, with the host in lower-case ASCII.</summary>
internal sealed record MessageLink(string Url, string Host);

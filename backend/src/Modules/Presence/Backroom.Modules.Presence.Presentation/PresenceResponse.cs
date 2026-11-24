using System.Text.Json.Serialization;

namespace Backroom.Modules.Presence.Presentation;

/// <summary><c>GET /presence</c>: the members among the asked accounts; others are left out.</summary>
internal sealed record PresenceResponse([property: JsonPropertyName("presence")] IReadOnlyList<PersonPresenceResponse> Presence);

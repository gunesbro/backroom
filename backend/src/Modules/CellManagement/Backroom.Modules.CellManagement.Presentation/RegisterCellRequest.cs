using System.Text.Json;

namespace Backroom.Modules.CellManagement.Presentation;

public sealed record RegisterCellRequest(string? RegistrationKey, JsonElement PublicKey, string? Version);

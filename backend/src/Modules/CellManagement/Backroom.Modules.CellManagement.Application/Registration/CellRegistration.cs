namespace Backroom.Modules.CellManagement.Application.Registration;

/// <summary>What a registered cell needs to get its tokens.</summary>
internal sealed record CellRegistration(string CellId, string ClientId, string Issuer);

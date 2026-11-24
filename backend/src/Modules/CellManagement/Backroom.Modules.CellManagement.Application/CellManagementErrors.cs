using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.CellManagement.Application;

internal static class CellManagementErrors
{
    /// <summary>The same answer for unknown, expired, used and differently claimed keys.</summary>
    public static readonly Error RegistrationKeyInvalid =
        Error.Validation("cell_management.registration_key_invalid", "The registration key is not valid.");

    public static readonly Error PublicKeyInvalid =
        Error.Validation("cell_management.public_key_invalid", "The public key must be an EC P-256 JSON Web Key with its public part only.");

    public static readonly Error CellInvalid =
        Error.Validation("cell_management.cell_invalid", "The cell id, name, type, region or public URL is not valid.");

    public static readonly Error SharedCellLimit =
        Error.Conflict("cell_management.shared_cell_limit", "The number of shared cells is at its limit.");

    public static readonly Error CellNotFound =
        Error.NotFound("cell_management.cell_not_found", "There is no cell with this id.");

    public static readonly Error CellExists =
        Error.Conflict("cell_management.cell_exists", "A cell with this id already exists.");
}

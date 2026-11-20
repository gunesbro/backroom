namespace Backroom.BuildingBlocks.Application.Identifiers;

/// <summary>Creates identifiers for new entities and messages.</summary>
public interface IIdGenerator
{
    /// <summary>A new UUIDv7: time-ordered at millisecond precision, unique without coordination.</summary>
    Guid NewId();
}

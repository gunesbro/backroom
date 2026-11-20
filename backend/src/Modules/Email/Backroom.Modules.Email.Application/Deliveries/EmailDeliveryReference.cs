namespace Backroom.Modules.Email.Application.Deliveries;

/// <summary>
/// What a delivery is for, such as an invitation and its code version: at most one delivery per template name and
/// reference is pending or sent (a unique index), so a redelivered event cannot send a second email. Ids only.
/// </summary>
internal readonly record struct EmailDeliveryReference
{
    public EmailDeliveryReference(Guid id, int version)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The reference id must not be empty.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        Id = id;
        Version = version;
    }

    public Guid Id { get; }

    public int Version { get; }
}

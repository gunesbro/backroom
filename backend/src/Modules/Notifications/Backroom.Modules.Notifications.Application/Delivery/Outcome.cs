namespace Backroom.Modules.Notifications.Application.Delivery;

/// <summary>What happens to one notification of one person.</summary>
internal enum Outcome
{
    /// <summary>Nothing: no bell entry, no live event.</summary>
    Drop,

    /// <summary>A bell entry without a live event (do not disturb, organization mute).</summary>
    BellOnly,

    /// <summary>A bell entry and a live event.</summary>
    Deliver,
}

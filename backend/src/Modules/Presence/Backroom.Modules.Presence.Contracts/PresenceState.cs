namespace Backroom.Modules.Presence.Contracts;

/// <summary>Automatic presence (docs/contracts/phase-2.md, flow 9). Never platform or device.</summary>
public enum PresenceState
{
    Offline = 0,
    Idle = 1,
    Online = 2,
}

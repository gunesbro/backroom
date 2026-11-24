namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>What Valkey holds for a revoked session or account: when it was revoked and until when the cell keeps it.</summary>
internal readonly record struct RevocationRecord(DateTimeOffset RevokedAt, DateTimeOffset KeepUntil);

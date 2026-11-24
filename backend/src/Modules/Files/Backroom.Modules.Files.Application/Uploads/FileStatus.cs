namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>
/// The states of docs/contracts/phase-2.md ("Data"), stored as their lower-case names. Step 21 uses pending and ready;
/// scanning (step 23), attached and deleted (step 22), rejected and expired (steps 23 and 24) are reserved in the schema.
/// </summary>
internal enum FileStatus
{
    Pending,
    Scanning,
    Ready,
    Rejected,
    Attached,
    Deleted,
    Expired,
}

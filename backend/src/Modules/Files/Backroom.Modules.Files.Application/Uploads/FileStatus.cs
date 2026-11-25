namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>
/// The states of docs/contracts/phase-2.md ("Data"), stored as their lower-case names. Completion moves a pending upload
/// to scanning; the worker's malware scan (step 23) moves it to ready, or to rejected when it is infected. Attached and
/// deleted (step 22) and expired (steps 23 and 24) are reserved in the schema.
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

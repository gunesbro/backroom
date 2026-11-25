using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// The <c>FileRejected</c> payload (docs/contracts/phase-2.md, "Live events"), sent only to the uploader's account group in one
/// organization after the scan rejected the upload. Ids and a reason code only: never the file name, type or content.
/// </summary>
[MessagePackObject]
public sealed record FileRejectedPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("accountId")] Guid AccountId,
    [property: Key("fileId")] Guid FileId,
    [property: Key("reason")] string Reason)
{
    /// <summary>The scanner found malware.</summary>
    public const string Malware = "malware";

    /// <summary>No scan answer in the allowed attempts, or the object was gone: nothing is served from the file.</summary>
    public const string ScanFailed = "scan_failed";
}

namespace Backroom.Modules.Files.Contracts;

/// <summary>
/// What a file is, for filters and icons (docs/contracts/phase-2.md, flow 6, step 3): decided from its content type and
/// confirmed by its first bytes when the upload completes. Stored as its lower-case name.
/// </summary>
public enum FileKind
{
    Other = 0,
    Document,
    Image,
    Spreadsheet,
    Archive,
}

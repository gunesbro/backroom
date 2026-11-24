using Backroom.Modules.Files.Contracts;

namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>The lower-case names kinds are stored and sent as.</summary>
internal static class FileKindNames
{
    public static string Of(FileKind kind) => kind switch
    {
        FileKind.Document => "document",
        FileKind.Image => "image",
        FileKind.Spreadsheet => "spreadsheet",
        FileKind.Archive => "archive",
        FileKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown file kind."),
    };
}

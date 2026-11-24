using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Backroom.Modules.Files.Contracts;

namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>
/// The content types an upload may declare (docs/contracts/phase-2.md, flow 6, step 1: an allow-list or
/// <c>application/octet-stream</c>), and the check at completion that the first bytes agree with the declared type.
/// SVG and HTML are not on the list: they can carry scripts. Files are always downloaded as attachments.
/// </summary>
internal static class FileTypes
{
    public const string OctetStream = "application/octet-stream";

    /// <summary>The bytes read from the start of an object to recognise its type.</summary>
    public const int SniffLength = 512;

    private static readonly FileType[] s_allowed =
    [
        new("image/png", FileKind.Image, ContentFamily.Png),
        new("image/jpeg", FileKind.Image, ContentFamily.Jpeg),
        new("image/gif", FileKind.Image, ContentFamily.Gif),
        new("image/webp", FileKind.Image, ContentFamily.WebP),
        new("application/pdf", FileKind.Document, ContentFamily.Pdf),
        new("text/plain", FileKind.Document, ContentFamily.Text),
        new("text/markdown", FileKind.Document, ContentFamily.Text),
        new("application/json", FileKind.Document, ContentFamily.Text),
        new("application/rtf", FileKind.Document, ContentFamily.Rtf),
        new("application/msword", FileKind.Document, ContentFamily.CompoundFile),
        new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", FileKind.Document, ContentFamily.Zip),
        new("application/vnd.oasis.opendocument.text", FileKind.Document, ContentFamily.Zip),
        new("application/vnd.ms-powerpoint", FileKind.Document, ContentFamily.CompoundFile),
        new("application/vnd.openxmlformats-officedocument.presentationml.presentation", FileKind.Document, ContentFamily.Zip),
        new("application/vnd.oasis.opendocument.presentation", FileKind.Document, ContentFamily.Zip),
        new("text/csv", FileKind.Spreadsheet, ContentFamily.Text),
        new("application/vnd.ms-excel", FileKind.Spreadsheet, ContentFamily.CompoundFile),
        new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FileKind.Spreadsheet, ContentFamily.Zip),
        new("application/vnd.oasis.opendocument.spreadsheet", FileKind.Spreadsheet, ContentFamily.Zip),
        new("application/zip", FileKind.Archive, ContentFamily.Zip),
        new("application/gzip", FileKind.Archive, ContentFamily.Gzip),
        new("application/x-7z-compressed", FileKind.Archive, ContentFamily.SevenZip),
        new("application/vnd.rar", FileKind.Archive, ContentFamily.Rar),
        new("video/mp4", FileKind.Other, ContentFamily.IsoMedia),
        new("video/quicktime", FileKind.Other, ContentFamily.IsoMedia),
        new("video/webm", FileKind.Other, ContentFamily.Ebml),
        new("audio/mpeg", FileKind.Other, ContentFamily.Mp3),
        new("audio/ogg", FileKind.Other, ContentFamily.Ogg),
        new("audio/wav", FileKind.Other, ContentFamily.Wave),
        new(OctetStream, FileKind.Other, ContentFamily.Any),
    ];

    private static readonly FrozenDictionary<string, FileType> s_byContentType =
        s_allowed.ToFrozenDictionary(type => type.ContentType, StringComparer.Ordinal);

    // What a file declared as octet-stream is stored as once its signature is recognised.
    private static readonly FrozenDictionary<ContentFamily, FileType> s_byFamily = new Dictionary<ContentFamily, FileType>
    {
        [ContentFamily.Png] = s_byContentType["image/png"],
        [ContentFamily.Jpeg] = s_byContentType["image/jpeg"],
        [ContentFamily.Gif] = s_byContentType["image/gif"],
        [ContentFamily.WebP] = s_byContentType["image/webp"],
        [ContentFamily.Pdf] = s_byContentType["application/pdf"],
        [ContentFamily.Zip] = s_byContentType["application/zip"],
        [ContentFamily.Gzip] = s_byContentType["application/gzip"],
        [ContentFamily.SevenZip] = s_byContentType["application/x-7z-compressed"],
        [ContentFamily.Rar] = s_byContentType["application/vnd.rar"],
        [ContentFamily.Rtf] = s_byContentType["application/rtf"],
    }.ToFrozenDictionary();

    /// <summary>The allowed type for a declared content type: a bare media type (no parameters), compared case-insensitively.</summary>
    public static bool TryGet(string? contentType, [NotNullWhen(true)] out FileType? type)
    {
        type = null;
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 128)
        {
            return false;
        }

        // Media types are ASCII; ToLowerInvariant never uses the current culture (Turkish İ/ı).
        return s_byContentType.TryGetValue(contentType.Trim().ToLowerInvariant(), out type);
    }

    /// <summary>
    /// The type stored for an upload declared as <paramref name="declared"/> whose content starts with
    /// <paramref name="head"/>, or <see langword="null"/> if the content does not match the declaration. A declared
    /// signature type needs that signature; text must carry no known signature and no NUL byte; octet-stream accepts
    /// anything and is stored as the recognised type when there is one.
    /// </summary>
    public static FileType? Confirm(FileType declared, ReadOnlySpan<byte> head)
    {
        ArgumentNullException.ThrowIfNull(declared);
        var detected = FileSignatures.Detect(head);
        return declared.Family switch
        {
            ContentFamily.Any => s_byFamily.GetValueOrDefault(detected) ?? declared,
            ContentFamily.Text => detected == ContentFamily.None && !head.Contains((byte)0) ? declared : null,
            _ => detected == declared.Family ? declared : null,
        };
    }
}

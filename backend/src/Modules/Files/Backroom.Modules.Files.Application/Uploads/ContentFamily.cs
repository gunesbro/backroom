namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>A group of content types that share the same leading bytes (a file signature).</summary>
internal enum ContentFamily
{
    /// <summary>No recognised signature: text, or bytes we do not know.</summary>
    None = 0,

    /// <summary>Declared only: <c>application/octet-stream</c> accepts any content.</summary>
    Any,

    /// <summary>Declared only: text, which must not start with a known signature or contain NUL bytes.</summary>
    Text,
    Png,
    Jpeg,
    Gif,
    WebP,
    Pdf,
    Zip,
    CompoundFile,
    Gzip,
    SevenZip,
    Rar,
    IsoMedia,
    Ebml,
    Mp3,
    Ogg,
    Wave,
    Rtf,
    Executable,
}

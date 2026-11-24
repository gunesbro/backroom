namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>Recognises a file's type from its first bytes (magic numbers).</summary>
internal static class FileSignatures
{
    public static ContentFamily Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ContentFamily.Png;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return ContentFamily.Jpeg;
        }

        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
        {
            return ContentFamily.Gif;
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8))
        {
            var format = head.Slice(8, 4);
            return format.SequenceEqual("WEBP"u8) ? ContentFamily.WebP
                : format.SequenceEqual("WAVE"u8) ? ContentFamily.Wave
                : ContentFamily.None;
        }

        if (head.StartsWith("%PDF-"u8))
        {
            return ContentFamily.Pdf;
        }

        // A local file header, or the end record of an empty archive.
        if (head.StartsWith((ReadOnlySpan<byte>)[0x50, 0x4B, 0x03, 0x04]) || head.StartsWith((ReadOnlySpan<byte>)[0x50, 0x4B, 0x05, 0x06]))
        {
            return ContentFamily.Zip;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            return ContentFamily.CompoundFile;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]))
        {
            return ContentFamily.Gzip;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
        {
            return ContentFamily.SevenZip;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0x52, 0x61, 0x72, 0x21, 0x1A, 0x07]))
        {
            return ContentFamily.Rar;
        }

        if (head.Length >= 8 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return ContentFamily.IsoMedia;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
        {
            return ContentFamily.Ebml;
        }

        if (head.StartsWith("OggS"u8))
        {
            return ContentFamily.Ogg;
        }

        // An ID3 tag, or an MPEG audio frame sync (11 set bits).
        if (head.StartsWith("ID3"u8) || (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0))
        {
            return ContentFamily.Mp3;
        }

        if (head.StartsWith((ReadOnlySpan<byte>)[0x7B, 0x5C, 0x72, 0x74, 0x66]))
        {
            return ContentFamily.Rtf;
        }

        return IsExecutable(head) ? ContentFamily.Executable : ContentFamily.None;
    }

    // Windows PE, ELF, Mach-O (32 and 64 bits, both byte orders) and universal binaries.
    private static bool IsExecutable(ReadOnlySpan<byte> head) =>
        head.StartsWith("MZ"u8)
        || head.StartsWith((ReadOnlySpan<byte>)[0x7F, 0x45, 0x4C, 0x46])
        || head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xED, 0xFA, 0xCE])
        || head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xED, 0xFA, 0xCF])
        || head.StartsWith((ReadOnlySpan<byte>)[0xCE, 0xFA, 0xED, 0xFE])
        || head.StartsWith((ReadOnlySpan<byte>)[0xCF, 0xFA, 0xED, 0xFE])
        || head.StartsWith((ReadOnlySpan<byte>)[0xCA, 0xFE, 0xBA, 0xBE]);
}

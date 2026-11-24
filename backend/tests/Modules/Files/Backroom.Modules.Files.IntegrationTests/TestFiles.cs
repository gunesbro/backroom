using System.Text;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>Small file contents with the right (or wrong) leading bytes.</summary>
internal static class TestFiles
{
    private static readonly byte[] s_pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Starts with the PNG signature; the rest does not matter to the type check.</summary>
    public static byte[] Png(int size = 600) => WithPrefix(s_pngSignature, size);

    public static byte[] Pdf(int size = 300) => WithPrefix("%PDF-1.7\n"u8.ToArray(), size);

    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    public static byte[] WindowsExecutable(int size = 300) => WithPrefix("MZ"u8.ToArray(), size);

    private static byte[] WithPrefix(byte[] prefix, int size)
    {
        var content = new byte[size];
        prefix.CopyTo(content, 0);
        for (var i = prefix.Length; i < size; i++)
        {
            content[i] = (byte)('a' + (i % 26));
        }

        return content;
    }
}

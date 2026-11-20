namespace Backroom.Modules.Organization.Application;

/// <summary>Whether text from the control plane fits the module's text columns before it is written.</summary>
internal static class ColumnText
{
    /// <summary>The column checks count code points (<c>char_length</c>); PostgreSQL text cannot hold NUL.</summary>
    public static bool Fits(string? text, int min, int max)
    {
        if (text is null || text.Length > max * 2 || text.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var codePoints = text.EnumerateRunes().Count();
        return codePoints >= min && codePoints <= max;
    }
}

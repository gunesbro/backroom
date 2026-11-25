namespace Backroom.Modules.Files.Application.Scanning;

/// <summary>
/// Malware scanning behind our own interface (CLAUDE.md, "Architecture rules"): ClamAV in development (a separate
/// container, so its license does not touch shipped code); the production scanner is chosen in the deploy phase.
/// </summary>
internal interface IFileScanner
{
    /// <summary>
    /// Scans <paramref name="content"/> of at most <paramref name="length"/> bytes. The stream is read once, in chunks,
    /// and never buffered whole. Any failure to get an answer (the scanner is down, too slow or refuses the size)
    /// throws <see cref="ScannerUnavailableException"/>: no answer is never "clean".
    /// </summary>
    Task<ScanVerdict> ScanAsync(Stream content, long length, CancellationToken cancellationToken);
}

namespace Backroom.Modules.Files.Application.Scanning;

/// <summary>The scanner gave no usable answer; the file stays unscanned and is retried (it never fails open).</summary>
internal sealed class ScannerUnavailableException : Exception
{
    public ScannerUnavailableException()
    {
    }

    public ScannerUnavailableException(string message)
        : base(message)
    {
    }

    public ScannerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// True when the scanner itself could not be reached (nothing was scanned), so the failure says nothing about the
    /// file and its attempt is not counted. False when the scanner took the file and failed or refused it.
    /// </summary>
    public bool IsOutage { get; init; }
}

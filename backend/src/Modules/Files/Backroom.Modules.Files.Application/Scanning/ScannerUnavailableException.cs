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
}

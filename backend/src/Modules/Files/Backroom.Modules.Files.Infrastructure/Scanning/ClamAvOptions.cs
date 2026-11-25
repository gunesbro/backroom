namespace Backroom.Modules.Files.Infrastructure.Scanning;

/// <summary>
/// Settings of the ClamAV daemon (clamd over TCP), section <c>Files:Scanner</c>. ClamAV runs as a separate process, so
/// its license does not touch our code. Validated at startup (<see cref="ClamAvOptionsValidator"/>).
/// </summary>
internal sealed class ClamAvOptions
{
    public const string SectionName = "Files:Scanner";

    /// <summary>The daemon's host name or address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The daemon's TCP port (3310 by default in the ClamAV image).</summary>
    public int Port { get; set; } = 3310;

    /// <summary>Time to connect.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Time for one whole scan (connect, streaming and the answer). The maintenance role's connection stays open
    /// meanwhile and PostgreSQL ends it after 60 idle seconds in a transaction, so this stays below that.
    /// </summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Bytes sent per INSTREAM chunk.</summary>
    public int ChunkSize { get; set; } = 64 * 1024;
}

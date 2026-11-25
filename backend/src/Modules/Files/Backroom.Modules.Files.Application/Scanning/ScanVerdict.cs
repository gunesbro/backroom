namespace Backroom.Modules.Files.Application.Scanning;

/// <summary>The scanner's answer. The name of what was found is not kept: it is of no use to anyone and is never logged.</summary>
internal enum ScanVerdict
{
    Clean,
    Infected,
}

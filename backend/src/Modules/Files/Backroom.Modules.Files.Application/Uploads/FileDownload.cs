namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>A signed URL that downloads the file until <paramref name="ExpiresAt"/>; never cached or logged.</summary>
internal sealed record FileDownload(Uri Url, DateTimeOffset ExpiresAt);

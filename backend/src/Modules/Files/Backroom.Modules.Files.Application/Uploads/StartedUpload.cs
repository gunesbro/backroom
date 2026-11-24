namespace Backroom.Modules.Files.Application.Uploads;

internal sealed record StartedUpload(Guid FileId, Uri UploadUrl, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

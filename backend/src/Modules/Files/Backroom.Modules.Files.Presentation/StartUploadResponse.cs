namespace Backroom.Modules.Files.Presentation;

/// <summary>A <c>PUT</c> of the file to <paramref name="UploadUrl"/> with exactly <paramref name="Headers"/>, before <paramref name="ExpiresAt"/>.</summary>
public sealed record StartUploadResponse(Guid FileId, Uri UploadUrl, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

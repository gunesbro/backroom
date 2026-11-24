namespace Backroom.Modules.Files.Application.Storage;

/// <summary>Where and how the client uploads: a <c>PUT</c> to <paramref name="Url"/> with exactly <paramref name="Headers"/>.</summary>
internal sealed record PresignedUpload(Uri Url, IReadOnlyDictionary<string, string> Headers);

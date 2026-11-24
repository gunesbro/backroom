namespace Backroom.Modules.Files.Application.Storage;

/// <summary>
/// Object storage behind our own interface (CLAUDE.md, "Architecture rules"): an S3-compatible store in development
/// (SeaweedFS) and in the deploy phase whatever the cell is configured with. Keys are chosen by the server and never
/// contain names; signed URLs are handed to clients only, never cached or logged.
/// </summary>
internal interface IFileStorage
{
    /// <summary>
    /// A URL the client uploads to with one <c>PUT</c> before <paramref name="expiresAt"/>, sending exactly the returned
    /// headers. The size and the content type are part of the signature, and the upload is refused if the key already
    /// holds an object, so a completed file can never be replaced through the same URL.
    /// </summary>
    Task<PresignedUpload> CreateUploadAsync(string key, long size, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>A URL that downloads the object before <paramref name="expiresAt"/>, always as an attachment of the given type.</summary>
    Task<Uri> CreateDownloadUrlAsync(string key, string contentType, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>The stored object's size, or <see langword="null"/> if there is none.</summary>
    Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken);

    /// <summary>Reads up to <paramref name="buffer"/>'s length from the start of the object; returns how many bytes were read.</summary>
    Task<int> ReadStartAsync(string key, Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Streams the whole object to <paramref name="consume"/> (never buffered whole) and returns its result; the stream is
    /// closed afterwards. A missing object throws <see cref="FileNotFoundException"/>.
    /// </summary>
    Task<T> ReadAsync<T>(string key, Func<Stream, Task<T>> consume, CancellationToken cancellationToken);

    /// <summary>Deletes the object; deleting a missing one is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

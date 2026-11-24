using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Files.Application.Access;
using Backroom.Modules.Files.Application.Storage;

namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>
/// Uploads, completion and signed downloads (docs/contracts/phase-2.md, flow 6, steps 1, 2, 3 and 5). The endpoint has
/// checked the membership (and <c>files.upload</c> for a start). The client uploads straight to storage; the cell only
/// signs, checks and records. Names are never logged; logs carry ids only.
/// </summary>
internal sealed partial class FileService(
    IFileStore files,
    IFileStorageProvider storageProvider,
    FileAccessCheck access,
    IIdGenerator ids,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<FileService> logger)
{
    /// <summary>100 MB per file for everyone (requirements, "Dosyalar, arama ve komut paleti").</summary>
    public const long MaxSize = 100L * 1024 * 1024;

    /// <summary>How long an upload URL stays valid (flow 6, step 1).</summary>
    public static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How long a download URL stays valid (flow 6, step 5; "Privacy rules": 60 seconds).</summary>
    public static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromSeconds(60);

    public static readonly Error UploadInvalid =
        Error.Validation("files.upload_invalid", "An upload needs a conversation and a size of at least 1 byte.");

    public static readonly Error TooLarge =
        Error.ContentTooLarge("files.too_large", "A file can be at most 100 MB.");

    public static readonly Error NameInvalid =
        Error.Validation("files.name_invalid", "The file name must be 1 to 255 characters without path separators or control characters.");

    public static readonly Error TypeNotAllowed =
        Error.Validation("files.type_not_allowed", "This content type is not allowed; use application/octet-stream for other files.");

    public static readonly Error ConversationNotFound =
        Error.NotFound("files.conversation_not_found", "The conversation was not found.");

    /// <summary>Unknown, refused and not yet usable files get the same answer, so it reveals nothing (flow 6, step 5).</summary>
    public static readonly Error NotFound =
        Error.NotFound("files.not_found", "The file was not found.");

    public static readonly Error UploadIncomplete =
        Error.Conflict("files.upload_incomplete", "The uploaded object is missing or its size differs from the declared size.");

    public static readonly Error TypeMismatch =
        Error.Conflict("files.type_mismatch", "The uploaded content does not match the declared content type.");

    public async Task<Result<StartedUpload>> StartAsync(
        Guid accountId, Guid? conversationId, string? name, long? size, string? contentType, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        if (conversationId is not { } conversation || conversation == Guid.Empty || size is not { } declaredSize || declaredSize < 1)
        {
            return UploadInvalid;
        }

        if (declaredSize > MaxSize)
        {
            return TooLarge;
        }

        if (!FileName.TryNormalize(name, out var normalizedName))
        {
            return NameInvalid;
        }

        if (!FileTypes.TryGet(contentType, out var type))
        {
            return TypeNotAllowed;
        }

        if (!await access.CanPostAsync(organizationId, conversation, accountId, cancellationToken))
        {
            return ConversationNotFound;
        }

        // The file quota's reservation (flow 12, step 24) goes here, in this transaction and before the row is written.
        var now = StoredTime.Now(timeProvider);
        var fileId = ids.NewId();
        var file = new NewFile(fileId, organizationId, accountId, conversation, normalizedName, declaredSize, type.ContentType, type.Kind, StorageKey(organizationId, fileId), now);
        await files.AddAsync(file, cancellationToken);

        var expiresAt = WholeSeconds(now) + UploadUrlLifetime;
        var upload = await storageProvider.ForOrganization(organizationId).CreateUploadAsync(file.StorageKey, declaredSize, type.ContentType, expiresAt, cancellationToken);
        LogStarted(logger, fileId, organizationId, accountId, declaredSize);
        return new StartedUpload(fileId, upload.Url, upload.Headers, expiresAt);
    }

    public async Task<Result<FileRecord>> CompleteAsync(Guid accountId, Guid fileId, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        // Only the uploader completes; anyone else learns nothing (the same answer as for an unknown file).
        if (await files.FindAsync(organizationId, fileId, cancellationToken) is not { } file || file.UploaderAccountId != accountId)
        {
            return NotFound;
        }

        if (file.Status != FileStatus.Pending)
        {
            // A repeat after a lost answer gets the stored file; refused and removed uploads stay unknown.
            return file.Status == FileStatus.Ready ? file : NotFound;
        }

        var storage = storageProvider.ForOrganization(organizationId);
        var storedSize = await storage.GetSizeAsync(file.StorageKey, cancellationToken);
        if (storedSize is null)
        {
            // Not uploaded yet: the upload can still be made while its URL is valid.
            return UploadIncomplete;
        }

        if (storedSize != file.Size)
        {
            // The object is removed, so the upload can be made again with the right content while its URL is valid.
            await storage.DeleteAsync(file.StorageKey, cancellationToken);
            LogRefused(logger, fileId, organizationId, "size");
            return UploadIncomplete;
        }

        if (!FileTypes.TryGet(file.DeclaredType, out var declared))
        {
            throw new InvalidOperationException("A stored file has a content type that is no longer allowed.");
        }

        var head = new byte[(int)Math.Min(FileTypes.SniffLength, file.Size)];
        var read = await storage.ReadStartAsync(file.StorageKey, head, cancellationToken);
        if (FileTypes.Confirm(declared, head.AsSpan(0, read)) is not { } confirmed)
        {
            await storage.DeleteAsync(file.StorageKey, cancellationToken);
            LogRefused(logger, fileId, organizationId, "type");
            return TypeMismatch;
        }

        // The malware scan (step 23) puts the file in "scanning" here instead; ready waits for its answer.
        var readyAt = StoredTime.Now(timeProvider);
        if (!await files.MarkReadyAsync(organizationId, fileId, confirmed.ContentType, confirmed.Kind, readyAt, cancellationToken))
        {
            // A concurrent completion won; answer with what it stored.
            return await files.FindAsync(organizationId, fileId, cancellationToken) is { Status: FileStatus.Ready } stored ? stored : NotFound;
        }

        LogCompleted(logger, fileId, organizationId, file.Size);
        return file with { DetectedType = confirmed.ContentType, Kind = confirmed.Kind, Status = FileStatus.Ready };
    }

    /// <summary>A short-lived signed URL, after checking access again: every download asks anew, so revoked access is refused at once.</summary>
    public async Task<Result<FileDownload>> DownloadAsync(Guid accountId, Guid fileId, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        if (await files.FindAsync(organizationId, fileId, cancellationToken) is not { } file
            || !await access.CanDownloadAsync(organizationId, file, accountId, cancellationToken))
        {
            LogDownloadRefused(logger, fileId, organizationId, accountId);
            return NotFound;
        }

        var expiresAt = WholeSeconds(timeProvider.GetUtcNow()) + DownloadUrlLifetime;
        var url = await storageProvider.ForOrganization(organizationId)
            .CreateDownloadUrlAsync(file.StorageKey, file.DetectedType ?? FileTypes.OctetStream, expiresAt, cancellationToken);
        LogDownload(logger, fileId, organizationId, accountId);
        return new FileDownload(url, expiresAt);
    }

    /// <summary>The storage key: ids only, so no name appears in keys or URLs (flow 6, step 1).</summary>
    internal static string StorageKey(Guid organizationId, Guid fileId) => $"{organizationId:D}/{fileId:D}";

    /// <summary>Signed URLs count their lifetime from a signing time in whole seconds; this keeps it at the lifetime, never above.</summary>
    private static DateTimeOffset WholeSeconds(DateTimeOffset time) => new(time.UtcTicks - (time.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");

    // Ids and sizes only: never the name or content.
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Upload {FileId} of {Size} bytes started in organization {OrganizationId} by account {AccountId}.")]
    private static partial void LogStarted(ILogger logger, Guid fileId, Guid organizationId, Guid accountId, long size);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Upload {FileId} of {Size} bytes completed in organization {OrganizationId}.")]
    private static partial void LogCompleted(ILogger logger, Guid fileId, Guid organizationId, long size);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Completion of upload {FileId} in organization {OrganizationId} refused: the {Check} check failed; the object was deleted.")]
    private static partial void LogRefused(ILogger logger, Guid fileId, Guid organizationId, string check);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Download of file {FileId} in organization {OrganizationId} signed for account {AccountId}.")]
    private static partial void LogDownload(ILogger logger, Guid fileId, Guid organizationId, Guid accountId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Download of file {FileId} in organization {OrganizationId} refused for account {AccountId}.")]
    private static partial void LogDownloadRefused(ILogger logger, Guid fileId, Guid organizationId, Guid accountId);
}

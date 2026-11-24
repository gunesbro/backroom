using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.BuildingBlocks.Presentation.Organizations;
using Backroom.Modules.Files.Application.Uploads;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Files.Presentation;

public static class FilesEndpoints
{
    /// <summary>The feature flag that turns on the file endpoints (docs/contracts/phase-2.md, "Decisions", 27).</summary>
    public const string FilesFlag = "Features:Files";

    /// <summary>A name of 255 characters (a few bytes each, more when JSON escapes them), a content type and ids.</summary>
    private const long MaxStartRequestBytes = 8 * 1024;

    public static IEndpointRouteBuilder MapFilesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/files/uploads", StartUploadAsync)
            .InOrganization()
            .RequirePermission(SystemRoles.FilesUpload)
            .WithMetadata(new RequestSizeLimitAttribute(MaxStartRequestBytes));
        // The uploader only; the service answers 404 to anyone else.
        endpoints.MapPost("/api/v1/orgs/{orgId:guid}/files/{fileId:guid}/complete", CompleteAsync)
            .InOrganization()
            .WithMetadata(new RequestSizeLimitAttribute(1024));
        // Access is checked again on every request; the signed URL is valid for 60 seconds.
        endpoints.MapGet("/api/v1/orgs/{orgId:guid}/files/{fileId:guid}/download", DownloadAsync).InOrganization();

        return endpoints;
    }

    private static async Task<IResult> StartUploadAsync(StartUploadRequest request, ICurrentAccount account, FileService files, CancellationToken cancellationToken)
    {
        var result = await files.StartAsync(account.AccountId, request.ConversationId, request.Name, request.Size, request.ContentType, cancellationToken);
        return result.ToHttpResult(upload => TypedResults.Created(
            (string?)null,
            new StartUploadResponse(upload.FileId, upload.UploadUrl, "PUT", upload.Headers, upload.ExpiresAt)));
    }

    private static async Task<IResult> CompleteAsync(Guid fileId, ICurrentAccount account, FileService files, CancellationToken cancellationToken)
    {
        var result = await files.CompleteAsync(account.AccountId, fileId, cancellationToken);
        return result.ToHttpResult(file => TypedResults.Ok(new FileResponse(
            file.Id, file.ConversationId, file.Name, file.Size, file.DetectedType ?? file.DeclaredType, FileKindNames.Of(file.Kind), "ready", file.CreatedAt)));
    }

    private static async Task<IResult> DownloadAsync(Guid fileId, ICurrentAccount account, FileService files, CancellationToken cancellationToken)
    {
        var result = await files.DownloadAsync(account.AccountId, fileId, cancellationToken);
        return result.ToHttpResult(download => new SignedUrlRedirect(download.Url));
    }
}

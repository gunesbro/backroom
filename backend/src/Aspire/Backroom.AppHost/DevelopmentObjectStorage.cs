namespace Backroom.AppHost;

/// <summary>
/// The development object storage (SeaweedFS's S3 API) and its credentials, which are generated secret parameters.
/// Each cell uses a bucket of its own.
/// </summary>
internal sealed record DevelopmentObjectStorage(
    EndpointReference S3Endpoint,
    IResourceBuilder<ContainerResource> Container,
    IResourceBuilder<ParameterResource> AccessKeyId,
    IResourceBuilder<ParameterResource> SecretAccessKey)
{
    public static string BucketFor(DevelopmentCell cell) => "backroom-cell-" + cell.Id;
}

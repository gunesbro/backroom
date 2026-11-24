using Amazon.Runtime;

namespace Backroom.Modules.Files.Infrastructure.Storage;

/// <summary>
/// Lets the AWS SDK send through <c>IHttpClientFactory</c> (CLAUDE.md, "Performance and resources"), which pools and
/// rotates the handlers; the SDK asks for a client per request and disposes it, which is cheap with the factory.
/// </summary>
internal sealed class SdkHttpClientFactory(IHttpClientFactory factory) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => factory.CreateClient(S3FileStorage.HttpClientName);

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
}

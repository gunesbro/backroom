using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Files.Application.Access;
using Backroom.Modules.Files.Application.Storage;
using Backroom.Modules.Files.Application.Uploads;
using Backroom.Modules.Files.Infrastructure.Storage;

namespace Backroom.Modules.Files.Infrastructure;

public static class FilesModule
{
    /// <summary>The module's organization-scoped schema, for the migrator.</summary>
    public static IServiceCollection AddFilesDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<FilesDbContext>(FilesDbContext.Schema, organizationScoped: true);

    /// <summary>
    /// Uploads, completion and signed downloads over the cell's S3-compatible storage. Needs the database session and
    /// Messaging's <c>IChannelReadAccess</c> and <c>IMessageFileAccess</c> (<c>AddMessaging</c>). The returned builder
    /// binds <see cref="S3StorageOptions"/>, checked at startup (<see cref="S3StorageOptionsValidator"/>): a missing
    /// key stops the service.
    /// </summary>
    public static OptionsBuilder<S3StorageOptions> AddFiles(this IServiceCollection services)
    {
        services.AddFilesDatabase();
        services.AddOutbox();
        services.Configure<OutboxRelayOptions>(options =>
        {
            if (!options.Schemas.Contains(FilesDbContext.Schema, StringComparer.Ordinal))
            {
                options.Schemas.Add(FilesDbContext.Schema);
            }
        });
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIdGenerator>(provider => new UuidV7IdGenerator(provider.GetRequiredService<TimeProvider>()));

        services.AddHttpClient(S3FileStorage.HttpClientName);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<S3StorageOptions>, S3StorageOptionsValidator>());
        services.TryAddSingleton<S3FileStorage>();
        services.TryAddSingleton<IFileStorageProvider, CellFileStorageProvider>();
        services.AddHostedService<DevelopmentBucketCreator>();

        services.TryAddScoped<IFileStore, FileStore>();
        services.TryAddScoped<FileAccessCheck>();
        services.TryAddScoped<FileService>();
        return services.AddOptions<S3StorageOptions>().ValidateOnStart();
    }
}

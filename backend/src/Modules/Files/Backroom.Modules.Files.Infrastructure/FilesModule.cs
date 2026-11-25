using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Files.Application.Access;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.Modules.Files.Application.Scanning;
using Backroom.Modules.Files.Application.Storage;
using Backroom.Modules.Files.Application.Uploads;
using Backroom.Modules.Files.Infrastructure.Scanning;
using Backroom.Modules.Files.Infrastructure.Storage;

namespace Backroom.Modules.Files.Infrastructure;

public static class FilesModule
{
    /// <summary>The module's organization-scoped schema, for the migrator.</summary>
    public static IServiceCollection AddFilesDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<FilesDbContext>(FilesDbContext.Schema, organizationScoped: true);

    /// <summary>
    /// Uploads, completion and signed downloads over the cell's S3-compatible storage. Needs the database session and
    /// Messaging's <c>IConversationReadAccess</c> and <c>IMessageFileAccess</c> (<c>AddMessaging</c>). The returned builder
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

        var storage = services.AddFileStorage();
        services.AddHostedService<DevelopmentBucketCreator>();

        services.TryAddScoped<IFileStore, FileStore>();
        services.TryAddScoped<FileAccessCheck>();
        services.TryAddScoped<IUploadLimitStore, UploadLimitStore>();
        services.TryAddScoped<UploadBrakes>();
        services.TryAddScoped<FileService>();
        return storage;
    }

    /// <summary>
    /// The malware scan of completed uploads, for the worker role: the scan job (maintenance role, docs/adr/0010) over the
    /// cell's storage and the ClamAV daemon (<c>Files:Scanner</c>, checked at startup). Needs <c>AddMaintenanceJobRunner</c>.
    /// The returned builder binds <see cref="S3StorageOptions"/> like <see cref="AddFiles"/>.
    /// </summary>
    public static OptionsBuilder<S3StorageOptions> AddFileScanning(this IServiceCollection services)
    {
        var storage = services.AddFileStorage();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ClamAvOptions>, ClamAvOptionsValidator>());
        services.AddOptions<ClamAvOptions>().BindConfiguration(ClamAvOptions.SectionName).ValidateOnStart();
        services.TryAddSingleton<IFileScanner, ClamAvScanner>();
        services.AddMaintenanceJob<FileScanJob>();
        return storage;
    }

    private static OptionsBuilder<S3StorageOptions> AddFileStorage(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        // The SDK retries and times out on its own (S3StorageOptions); the hosts' default resilience handler would
        // multiply its attempts while a completion holds a pooled database connection.
        // Experimental API (EXTEXP0001); if it changes, the completion budget in FileService still bounds the time.
#pragma warning disable EXTEXP0001
        services.AddHttpClient(S3FileStorage.HttpClientName).RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<S3StorageOptions>, S3StorageOptionsValidator>());
        services.TryAddSingleton<S3FileStorage>();
        services.TryAddSingleton<IFileStorageProvider, CellFileStorageProvider>();
        return services.AddOptions<S3StorageOptions>().ValidateOnStart();
    }
}

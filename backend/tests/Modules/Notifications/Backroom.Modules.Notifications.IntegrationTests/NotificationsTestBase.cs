using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Notifications.Application.Bell;
using Backroom.Modules.Notifications.Application.Delivery;
using Backroom.Modules.Notifications.Application.Settings;
using Backroom.Modules.Notifications.Infrastructure;
using Backroom.Modules.Presence.Contracts;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Notifications.IntegrationTests;

/// <summary>
/// The module's services over real PostgreSQL (row-level security on, the application role) with fake neighbours: Messaging's
/// read access and previews, Presence's reader and Realtime's publisher. Each test uses organizations and accounts of its own.
/// </summary>
public abstract class NotificationsTestBase(PostgresDatabase database) : IAsyncLifetime
{
    private static readonly SemaphoreSlim s_migration = new(1, 1);
    private static bool s_migrated;

    private ServiceProvider _provider = null!;

    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    protected FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));

    internal FakeConversationAccess Access { get; } = new();

    internal FakeMembers Members { get; } = new();

    internal FakePresence Presence { get; } = new();

    internal RecordingPublisher Published { get; } = new();

    internal FakePreviews Previews { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await MigrateOnceAsync();
        Previews.Access = Access;
        Access.Members = Members;
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.AddDatabaseSession().Configure(options => options.ConnectionString = database.ApplicationConnectionString());
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IConversationReadAccess>(Access);
        services.AddSingleton<IPresenceReader>(Presence);
        services.AddSingleton<Backroom.BuildingBlocks.Application.Organizations.IOrganizationMembershipCheck>(Members);
        services.AddScoped<Backroom.BuildingBlocks.Application.Messaging.AfterCommitActions>();
        services.AddSingleton<ILiveEventPublisher>(Published);
        services.AddSingleton<IMessagePreviews>(Previews);
        services.AddNotifications();
        services.AddScoped<ActivityAddedHandler>();
        _provider = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs <paramref name="work"/> in one unit of work of the organization, as an endpoint or a consumer does.</summary>
    internal async Task<T> InOrganizationAsync<T>(Guid organizationId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var result = await work(scope.ServiceProvider);
        await unitOfWork.CommitAsync(CancellationToken);
        // As the consumer host does: after-commit work runs once the commit has succeeded.
        await scope.ServiceProvider.GetRequiredService<Backroom.BuildingBlocks.Application.Messaging.AfterCommitActions>().RunAsync(CancellationToken.None);
        return result;
    }

    internal Task HandleAsync(Guid organizationId, ActivityAdded activity) =>
        InOrganizationAsync(organizationId, async services =>
        {
            await services.GetRequiredService<ActivityAddedHandler>().HandleAsync(activity, CancellationToken);
            return 0;
        });

    /// <summary>The handler runs, then the unit of work is rolled back (a failed commit): no after-commit work may run.</summary>
    internal async Task HandleWithoutCommitAsync(Guid organizationId, ActivityAdded activity)
    {
        await using var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        await scope.ServiceProvider.GetRequiredService<ActivityAddedHandler>().HandleAsync(activity, CancellationToken);
        await unitOfWork.RollbackAsync(CancellationToken);
    }

    internal ActivityAdded Activity(Guid organizationId, string kind, Guid? conversationId, Guid? messageId, Guid actor, params Guid[] recipients) =>
        new(Guid.CreateVersion7(), organizationId, Clock.GetUtcNow(), kind, conversationId, messageId, actor, recipients);

    internal Task<BellPage> ListAsync(Guid organizationId, Guid accountId, string? tab = null, string? after = null, int? limit = null) =>
        InOrganizationAsync(organizationId, async services =>
        {
            var result = await services.GetRequiredService<BellService>().ListAsync(accountId, tab, after, limit, CancellationToken);
            return result.GetValueOrThrow();
        });

    internal Task<AccountSettings> SaveAccountSettingsAsync(Guid organizationId, Guid accountId, string level, DateTimeOffset? mutedUntil = null, bool hidePreviews = false) =>
        InOrganizationAsync(organizationId, async services =>
        {
            var result = await services.GetRequiredService<SettingsService>().PutAccountAsync(accountId, level, mutedUntil, true, hidePreviews, CancellationToken);
            return result.GetValueOrThrow();
        });

    internal Task<ConversationSettings> SaveConversationSettingsAsync(
        Guid organizationId, Guid accountId, Guid conversationId, string level, bool followedThreadReplies = true, bool ignoreGroupMentions = false)
    {
        Access.Allow(conversationId, accountId);
        return InOrganizationAsync(organizationId, async services =>
        {
            var result = await services.GetRequiredService<SettingsService>()
                .PutConversationAsync(accountId, conversationId, level, followedThreadReplies, ignoreGroupMentions, true, CancellationToken);
            return result.GetValueOrThrow();
        });
    }

    [SuppressMessage("Security", "CA2100", Justification = "The SQL comes from the tests; values are parameters.")]
    protected async Task<long> CountAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    private async Task MigrateOnceAsync()
    {
        await s_migration.WaitAsync(CancellationToken);
        try
        {
            if (s_migrated)
            {
                return;
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNotificationsDatabase();
            services.AddDatabaseMigrations(allowBootstrap: false).Configure(options => options.MigratorConnectionString = database.MigratorConnectionString);
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);
            s_migrated = true;
        }
        finally
        {
            s_migration.Release();
        }
    }
}

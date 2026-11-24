using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure;

public static class RealtimeModule
{
    /// <summary>
    /// Live events for the modules. Only the no-op publisher exists so far; the Valkey and SignalR implementation
    /// replaces it in step 6.2, so modules can call <see cref="ILiveEventPublisher"/> already.
    /// </summary>
    public static IServiceCollection AddRealtime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ILiveEventPublisher, NoOpLiveEventPublisher>();
        return services;
    }
}

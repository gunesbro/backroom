using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Email.Application.Deliveries;
using Backroom.Modules.Email.Application.Invitations;
using Backroom.Modules.Email.Application.Sending;
using Backroom.Modules.Email.Infrastructure.Deliveries;
using Backroom.Modules.Email.Infrastructure.Sending;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.Email.Infrastructure;

public static class EmailModule
{
    /// <summary>The module's organization-scoped schema (delivery log and inbox), for the migrator.</summary>
    public static IServiceCollection AddEmailDatabase(this IServiceCollection services) =>
        services.AddModuleDbContext<EmailDbContext>(EmailDbContext.Schema, organizationScoped: true);

    /// <summary>
    /// Sending email (worker role only) and its delivery log. Needs the host's database session and
    /// <see cref="IHostEnvironment"/>. The returned builder binds <see cref="EmailOptions"/>, which are checked at
    /// startup (<see cref="EmailOptionsValidator"/>): outside Production the service does not start unless the
    /// target is Mailpit.
    /// </summary>
    public static OptionsBuilder<EmailOptions> AddEmail(this IServiceCollection services)
    {
        services.AddEmailDatabase();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>());
        services.TryAddSingleton(provider => EmailTransport.Resolve(
            provider.GetRequiredService<IOptions<EmailOptions>>().Value,
            provider.GetRequiredService<IHostEnvironment>().IsProduction()));
        services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
        services.TryAddSingleton<IRecipientHasher, RecipientHasher>();
        services.TryAddScoped<IDeliveryLog, DeliveryLog>();
        services.TryAddSingleton<EmailDispatcher>();
        services.AddHostedService<EmailStartupReport>();

        // The invitation email; needs Organization's IInvitationLinks with the web origin (AddOrganizationInvitationLinks).
        services.AddIntegrationEventHandler<InvitationReady, InvitationReadyConsumer>(EmailDbContext.Schema);
        return services.AddOptions<EmailOptions>().ValidateOnStart();
    }
}

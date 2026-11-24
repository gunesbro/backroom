namespace Backroom.AppHost;

/// <summary>Backing services the hosts are wired to.</summary>
internal sealed record DevelopmentResources(
    IResourceBuilder<KeycloakResource> Keycloak,
    IResourceBuilder<PostgresServerResource> Postgres,
    IResourceBuilder<ParameterResource> ControlPlaneAdminSecret,
    IResourceBuilder<RabbitMQServerResource> RabbitMq,
    IResourceBuilder<MailPitContainerResource> Mailpit,
    IResourceBuilder<ValkeyResource> Valkey);

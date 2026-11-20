namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ connection settings. The connection string (an <c>amqp://</c> or <c>amqps://</c> URI)
/// carries a password, so it comes from user secrets or the environment, never from appsettings files.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Shown in the RabbitMQ management UI, e.g. the service and role name.</summary>
    public string ClientName { get; set; } = "backroom";
}

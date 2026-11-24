namespace Backroom.AppHost;

/// <summary>
/// Creates one RabbitMQ virtual host per cell. Cells share the development broker, and exchange and queue names do not
/// carry the cell id, so each cell needs its own namespace. A short-lived container calls the management API
/// (the plugin is enabled) and exits; cell workers wait for it to complete.
/// </summary>
internal static class DevelopmentVirtualHosts
{
    public const string CurlImage = "curlimages/curl";
    public const string CurlImageTag = "8.11.1";

    public static IResourceBuilder<ContainerResource> AddRabbitMqVirtualHosts(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<RabbitMQServerResource> rabbitMq,
        IEnumerable<DevelopmentCell> cells)
    {
        // The vhost name and user come from our own constants and the generated user name; the password stays in the environment.
        var script = string.Join(
            " && ",
            cells.Select(cell =>
                $"curl -fsS --retry 30 --retry-delay 2 --retry-all-errors -u \"$RMQ_USER:$RMQ_PASSWORD\" -X PUT http://rabbitmq:15672/api/vhosts/{cell.VirtualHost}" +
                $" && curl -fsS -u \"$RMQ_USER:$RMQ_PASSWORD\" -X PUT -H 'content-type: application/json' " +
                $"-d '{{\"configure\":\".*\",\"write\":\".*\",\"read\":\".*\"}}' http://rabbitmq:15672/api/permissions/{cell.VirtualHost}/$RMQ_USER"));

        return builder.AddContainer("rabbitmq-vhosts", CurlImage, CurlImageTag)
            .WithEntrypoint("/bin/sh")
            .WithArgs("-c", script)
            .WithEnvironment("RMQ_USER", rabbitMq.Resource.UserNameReference)
            .WithEnvironment("RMQ_PASSWORD", rabbitMq.Resource.PasswordParameter)
            .WaitFor(rabbitMq);
    }
}

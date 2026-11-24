using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Modules.Realtime.Infrastructure;

namespace Backroom.Modules.Messaging.IntegrationTests;

public sealed class RealtimeRegistrationTests
{
    [Fact]
    public async Task AddRealtime_RegistersAPublisherThatAcceptsAndDropsEventsAsync()
    {
        var services = new ServiceCollection().AddRealtime();
        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<ILiveEventPublisher>();
        var cancellationToken = TestContext.Current.CancellationToken;

        var message = new MessagePostedPayload(
            Guid.NewGuid(), Guid.NewGuid(), new LiveMessage(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), "x", DateTimeOffset.UtcNow));
        var channel = new ChannelCreatedPayload(Guid.NewGuid(), new LiveChannel(Guid.NewGuid(), "x", DateTimeOffset.UtcNow));

        await publisher.PublishAsync(message, cancellationToken);
        await publisher.PublishAsync(channel, cancellationToken);
    }

    [Fact]
    public void AddRealtime_KeepsAnotherPublisherThatWasRegisteredFirst()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILiveEventPublisher>(new RecordingLiveEventPublisher());
        services.AddRealtime();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ILiveEventPublisher>().Should().BeOfType<RecordingLiveEventPublisher>();
    }
}

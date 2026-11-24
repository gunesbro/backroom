using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Modules.Realtime.Infrastructure;

namespace Backroom.Modules.Messaging.IntegrationTests;

public sealed class RealtimeRegistrationTests
{
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

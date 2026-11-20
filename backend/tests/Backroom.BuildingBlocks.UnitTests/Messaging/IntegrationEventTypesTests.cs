using System.Text;
using AwesomeAssertions;
using Backroom.BuildingBlocks.Domain.Events;
using Backroom.BuildingBlocks.Infrastructure.Messaging;

namespace Backroom.BuildingBlocks.UnitTests.Messaging;

public sealed class IntegrationEventTypesTests
{
    [Theory]
    [InlineData("organization.member-added.v1", "organization")]
    [InlineData("messaging.message-posted.v1", "messaging")]
    [InlineData("control_plane_connector.cell-registered.v12", "control_plane_connector")]
    public void ModuleOf_WhenEventTypeIsValid_ReturnsTheModule(string eventType, string module)
    {
        IntegrationEventTypes.ModuleOf(eventType).Should().Be(module);
        IntegrationEventTypes.ExchangeFor(module).Should().Be(module + ".events");
    }

    [Theory]
    [InlineData("")]
    [InlineData("organization.member-added")]
    [InlineData("organization.member_added.v1")]
    [InlineData("Organization.member-added.v1")]
    [InlineData("organization.member-added.v0")]
    [InlineData("organization.member--added.v1")]
    [InlineData("organization.member-added.v1.extra")]
    [InlineData("organizatıon.member-added.v1")]
    public void Validate_WhenEventTypeIsMalformed_Throws(string eventType)
    {
        var validate = () => IntegrationEventTypes.Validate(eventType);

        validate.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Serialize_WhenCalled_UsesSnakeCaseNames()
    {
        var sample = new SampleEvent(Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, "Admin");

        var json = Encoding.UTF8.GetString(IntegrationEventSerializer.Serialize(sample));

        json.Should().Contain("\"event_id\":").And.Contain("\"org_id\":").And.Contain("\"occurred_at\":").And.Contain("\"role_key\":\"Admin\"");
        IntegrationEventSerializer.Deserialize<SampleEvent>(Encoding.UTF8.GetBytes(json)).Should().Be(sample);
    }

    private sealed record SampleEvent(Guid EventId, Guid OrgId, DateTimeOffset OccurredAt, string RoleKey) : IIntegrationEvent
    {
        public static string EventType => "organization.sample-happened.v1";
    }
}

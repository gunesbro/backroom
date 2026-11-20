using System.Net.Sockets;
using AwesomeAssertions;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Messaging;

namespace Backroom.BuildingBlocks.UnitTests.Messaging;

public sealed class IntegrationEventConsumerHostTests
{
    public static TheoryData<Exception, bool> Exceptions => new()
    {
        { new NpgsqlException("Connection refused.", new SocketException()), true },
        { new InvalidOperationException("Wrapped.", new TimeoutException()), true },
        { new TimeoutException(), true },
        { new InvalidOperationException("Handler bug."), false },
        { new NpgsqlException("Not transient."), false },
        { new ArgumentException("Bad data."), false },
    };

    [Theory]
    [MemberData(nameof(Exceptions))]
    public void IsTransient_ForException_ClassifiesInfrastructureFailures(Exception exception, bool transient)
    {
        IntegrationEventConsumerHost.IsTransient(exception).Should().Be(transient);
    }

    [Fact]
    public void DescribeFailure_ForDatabaseError_AddsCodeConstraintAndTableButNoMessage()
    {
        var postgres = new PostgresException(
            "duplicate key value violates unique constraint; Key (email)=(ayse@example.test) already exists.",
            "ERROR", "ERROR", "23505", schemaName: "organization", tableName: "members", constraintName: "members_email_key");
        var exception = new InvalidOperationException("Saving failed for ayse@example.test.", postgres);

        var description = IntegrationEventConsumerHost.DescribeFailure(exception);

        description.Should().Be("InvalidOperationException (SQLSTATE 23505, constraint members_email_key, table organization.members)");
    }

    [Fact]
    public void StackTraceWithoutMessages_ForExceptionChain_KeepsTypesAndLocationsOnly()
    {
        Exception exception;
        try
        {
            try
            {
                throw new ArgumentException("Inner message with ayse@example.test.");
            }
            catch (ArgumentException inner)
            {
                throw new InvalidOperationException("Outer message with ayse@example.test.", inner);
            }
        }
        catch (InvalidOperationException outer)
        {
            exception = outer;
        }

        var text = IntegrationEventConsumerHost.StackTraceWithoutMessages(exception);

        text.Should().Contain("System.InvalidOperationException").And.Contain("--- caused by System.ArgumentException")
            .And.Contain(nameof(StackTraceWithoutMessages_ForExceptionChain_KeepsTypesAndLocationsOnly))
            .And.NotContain("ayse@example.test");
    }
}

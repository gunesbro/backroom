using AwesomeAssertions;
using Backroom.BuildingBlocks.Application.Organizations;

namespace Backroom.BuildingBlocks.UnitTests.Organizations;

public sealed class OrganizationContextTests
{
    [Fact]
    public void OrganizationId_WhenNotEntered_IsNull()
    {
        new OrganizationContext().OrganizationId.Should().BeNull();
    }

    [Fact]
    public void Enter_WhenCalled_SetsTheOrganization()
    {
        var context = new OrganizationContext();
        var organizationId = Guid.CreateVersion7();

        context.Enter(organizationId);

        context.OrganizationId.Should().Be(organizationId);
    }

    [Fact]
    public void Enter_WhenSameOrganizationAgain_KeepsIt()
    {
        var context = new OrganizationContext();
        var organizationId = Guid.CreateVersion7();
        context.Enter(organizationId);

        context.Enter(organizationId);

        context.OrganizationId.Should().Be(organizationId);
    }

    [Fact]
    public void Enter_WhenAnotherOrganization_Throws()
    {
        var context = new OrganizationContext();
        var first = Guid.CreateVersion7();
        context.Enter(first);

        var enter = () => context.Enter(Guid.CreateVersion7());

        enter.Should().Throw<InvalidOperationException>();
        context.OrganizationId.Should().Be(first);
    }

    [Fact]
    public void Enter_WhenEmpty_Throws()
    {
        var enter = () => new OrganizationContext().Enter(Guid.Empty);

        enter.Should().Throw<ArgumentException>();
    }
}

using AwesomeAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// The <c>away</c> field of <c>AccountStatusChanged</c> is additive (docs/contracts/phase-2.md, "Cell ↔ control plane link"): a cell
/// built before it reads the same message as before, and this cell reads a message from a control plane built before it as "not away".
/// </summary>
public sealed class AccountStatusWireTests
{
    private const int AwayField = 8;

    [Fact]
    public void AccountStatusChanged_WithAway_IsTheOldMessagePlusOneFieldAnOlderCellSkips()
    {
        var message = Message();
        var before = message.ToByteArray();
        message.Away = true;
        var after = message.ToByteArray();

        // The seven fields an older cell knows are byte for byte what they were; away is one more varint field at the end.
        after.Should().StartWith(before);
        after.Skip(before.Length).Should().Equal((byte)(AwayField << 3), (byte)1);

        // An older cell's reader: the fields it knows are read, an unknown one is skipped without an error.
        var known = new List<int>();
        var input = new CodedInputStream(after);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = WireFormat.GetTagFieldNumber(tag);
            if (field < AwayField)
            {
                known.Add(field);
            }

            input.SkipLastField();
        }

        input.IsAtEnd.Should().BeTrue();
        known.Should().Equal(1, 2, 3, 6, 7);
    }

    [Fact]
    public void AccountStatusChanged_WithoutAway_PutsNothingNewOnTheWire()
    {
        var message = Message();

        message.ToByteArray().Should().NotContain((byte)(AwayField << 3), "false is the default and is not written, so nothing changes for a cell that is not away-aware");
    }

    [Fact]
    public void AccountStatusChanged_FromAnOlderControlPlane_IsReadAsNotAway()
    {
        // The bytes an older control plane sends have no field 8.
        var parsed = AccountStatusChanged.Parser.ParseFrom(Message().ToByteArray());

        AccountStatusChangedHandler.TryRead(parsed, out var status).Should().BeTrue();
        status!.Away.Should().BeFalse();
    }

    [Fact]
    public void AccountStatusChanged_WithAway_IsReadByThisCell()
    {
        var message = Message();
        message.Away = true;

        AccountStatusChangedHandler.TryRead(AccountStatusChanged.Parser.ParseFrom(message.ToByteArray()), out var status).Should().BeTrue();
        status!.Away.Should().BeTrue();
    }

    // Values whose bytes never contain 0x40, so the "nothing new on the wire" check cannot be fooled by a payload byte.
    private static AccountStatusChanged Message() => new()
    {
        AccountId = "0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
        DndUntil = new Timestamp { Seconds = 1, Nanos = 0 },
        CustomEmoji = "x",
        TimeZone = "UTC",
        Version = 3,
    };
}

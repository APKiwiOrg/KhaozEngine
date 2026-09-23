using System;
using KhaozEngine.ItemInstances.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerCommitFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>Stored operation bodies are read without changing their version 1 bytes or guessing at
/// missing instance outcomes.</summary>
public sealed class ContainerOperationEventCodecTests
{
    public static TheoryData<ContainerOperation> LegacyOperations => new()
    {
        ContainerOperation.Move(Bank, 2, Bag, 4, 1, Instance),
        ContainerOperation.Split(Bank, 2, 4, 3),
        ContainerOperation.Merge(Bank, 2, 4, Instance, Instance + 1),
        ContainerOperation.Grant(Bag, 4, Sword, 1),
        ContainerOperation.Take(Bank, 2, 1, Instance),
    };

    [Theory]
    [MemberData(nameof(LegacyOperations))]
    public void A_legacy_operation_decodes_to_its_exact_canonical_fields(ContainerOperation expected)
    {
        byte[] body = expected.ToCanonicalArray();

        Assert.True(ContainerOperationEventCodec.TryRead(expected.EventType, 1, body,
            out ContainerOperation read, out string? reason), reason);

        Assert.Equal(expected.Kind, read.Kind);
        Assert.Equal(expected.Container, read.Container);
        Assert.Equal(expected.Slot, read.Slot);
        Assert.Equal(expected.DestinationContainerOrOwn, read.DestinationContainerOrOwn);
        Assert.Equal(expected.DestinationSlot, read.DestinationSlot);
        Assert.Equal(expected.DefinitionId, read.DefinitionId);
        Assert.Equal(expected.Count, read.Count);
        Assert.Equal(expected.InstanceId, read.InstanceId);
        Assert.Equal(expected.DestinationInstanceId, read.DestinationInstanceId);
        Assert.Equal(body, read.ToCanonicalArray());
    }

    [Fact]
    public void The_version_one_move_body_has_its_original_bytes()
    {
        ContainerOperation move = ContainerOperation.Move(Bank, 2, Bag, 4, 1, Instance);

        Assert.Equal(
            [0x01, 0x04, 0x62, 0x61, 0x6E, 0x6B, 0x02, 0x03, 0x62, 0x61, 0x67, 0x04, 0x01, 0xD9, 0x36],
            move.ToCanonicalArray());
    }

    [Fact]
    public void Malformed_event_bodies_refuse_without_throwing()
    {
        byte[] move = ContainerOperation.Move(Bank, 2, Bag, 4, 1, Instance).ToCanonicalArray();

        Refused(ItemInstanceEvents.Moved, 1, move[..^1], "event-truncated");
        Refused(ItemInstanceEvents.Moved, 1, [0x81, 0x00], "event-varint");
        Refused(ItemInstanceEvents.Moved, 1, [0x01, 0x01, 0xFF], "event-utf8");
        Refused(ItemInstanceEvents.Moved, 1, [.. move, 0x00], "event-trailing");
        Refused(ItemInstanceEvents.Taken, 1, move, "event-kind");
        Refused(ItemInstanceEvents.Moved, 2, move, "event-version");
        Refused(ItemInstanceEvents.Moved, 1, [0x7F], "event-kind");
    }

    [Fact]
    public void A_version_one_instance_grant_and_craft_report_the_missing_replay_data()
    {
        byte[] grant = ContainerOperation.Grant(Bag, 4, Sword, 1, Instance, Payload()).ToCanonicalArray();
        Refused(ItemInstanceEvents.Granted, 1, grant, "legacy-payload-omitted");

        var craft = new ItemCraftedEvent(1, Instance, 1, Payload(), Payload(43));
        byte[] audit = craft.ToArray();
        Assert.True(ItemCraftedEvent.TryRead(audit, out _, out string? reason), reason);
        Refused(ItemInstanceEvents.Crafted, 1, audit, "legacy-location-omitted");
    }

    static void Refused(string eventType, int schemaVersion, byte[] body, string expectedReason)
    {
        Assert.False(ContainerOperationEventCodec.TryRead(eventType, schemaVersion, body,
            out ContainerOperation read, out string? reason));
        Assert.Equal(expectedReason, reason);
        Assert.Equal(default, read);
    }
}

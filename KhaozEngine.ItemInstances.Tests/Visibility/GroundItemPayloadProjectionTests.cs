using System;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Visibility;

/// <summary>
/// The one projection a stored item passes before its bytes are seated on a public ground component. It is
/// the container projection at <see cref="PropertyVisibility.Everyone"/>, including quarantine handling.
/// </summary>
public sealed class GroundItemPayloadProjectionTests
{
    [Fact]
    public void Stored_private_fields_do_not_reach_the_ground_and_the_output_is_canonical()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] stored = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);
        var projected = new byte[stored.Length];

        int written = GroundItemPayloadProjection.Project(
            registry, stored, quarantined: false, identified: true, revealedMask: 0, projected);

        ReadOnlySpan<byte> view = projected.AsSpan(0, written);
        Assert.InRange(written, 1, ItemInstancePayload.MaxInstancePayloadBytes);
        Assert.Null(ItemInstancePayload.Validate(registry, view));
        Assert.False(VisibilityFixtures.Carries(registry, view, VisibilityFixtures.ServerSecret));
        Assert.False(VisibilityFixtures.Carries(registry, view, VisibilityFixtures.OwnerSecret));
        Assert.False(VisibilityFixtures.Carries(registry, view, InstancePropertyKind.Charges));
        Assert.False(VisibilityFixtures.Carries(registry, view, InstancePropertyKind.Durability));
        Assert.False(VisibilityFixtures.Carries(registry, view, InstancePropertyKind.BoundTo));

        byte[] nested = VisibilityFixtures.SocketNested(registry, view);
        Assert.True(VisibilityFixtures.Carries(registry, nested, InstancePropertyKind.ItemLevel));
        Assert.False(VisibilityFixtures.Carries(registry, nested, InstancePropertyKind.Durability));
        Assert.False(VisibilityFixtures.Carries(registry, nested, InstancePropertyKind.BoundTo));
    }

    [Fact]
    public void An_ordinary_ground_payload_is_byte_identical_to_the_container_everyone_view()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] stored = VisibilityFixtures.EveryKind(identified: false, revealedMask: 0);
        ContainerPageChange entry = Entry(stored, quarantined: false, identified: false, revealedMask: 0);
        var expected = new byte[stored.Length];
        int expectedBytes = ContainerPageProjection.ProjectPayload(
            registry, entry, PropertyVisibility.Everyone, expected);
        var actual = new byte[stored.Length];

        int actualBytes = GroundItemPayloadProjection.Project(
            registry, stored, quarantined: false, identified: false, revealedMask: 0, actual);

        Assert.Equal(expectedBytes, actualBytes);
        Assert.True(expected.AsSpan(0, expectedBytes).SequenceEqual(actual.AsSpan(0, actualBytes)));
    }

    [Fact]
    public void A_quarantined_ground_payload_is_the_same_hollow_wrapper_as_the_container_view()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        // A quarantined wrapper may preserve bytes above the live payload cap. Its public hollow form must
        // still fit the ground component's mirrored cap.
        byte[] original = new byte[ItemInstancePayload.MaxInstancePayloadBytes + 37];
        byte[] stored = QuarantineWrapper.Wrap(InstanceQuarantineReason.UnknownDefinition, 37, original);
        ContainerPageChange entry = Entry(stored, quarantined: true, identified: true, revealedMask: ulong.MaxValue);
        var expected = new byte[stored.Length];
        int expectedBytes = ContainerPageProjection.ProjectPayload(
            registry, entry, PropertyVisibility.Everyone, expected);
        var actual = new byte[stored.Length];

        int actualBytes = GroundItemPayloadProjection.Project(
            registry, stored, quarantined: true, identified: true, revealedMask: ulong.MaxValue, actual);

        Assert.Equal(expectedBytes, actualBytes);
        Assert.InRange(actualBytes, 1, ItemInstancePayload.MaxInstancePayloadBytes);
        Assert.True(expected.AsSpan(0, expectedBytes).SequenceEqual(actual.AsSpan(0, actualBytes)));
        ReadOnlySpan<byte> hollow = actual.AsSpan(0, actualBytes);
        Assert.True(QuarantineWrapper.Verify(hollow));
        Assert.True(QuarantineWrapper.TryUnwrap(
            hollow, out ReadOnlySpan<byte> preserved, out string? reason, out int stampedVersion));
        Assert.Equal(InstanceQuarantineReason.UnknownDefinition, reason);
        Assert.Equal(37, stampedVersion);
        Assert.True(preserved.IsEmpty);
    }

    [Fact]
    public void Malformed_stored_bytes_are_refused_exactly_as_the_container_projection_refuses_them()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] malformedPayload = [0x05, 0x02, 0x5A, 0x64, 0x02, 0x01, 0x44];
        ContainerPageChange live = Entry(
            malformedPayload, quarantined: false, identified: true, revealedMask: ulong.MaxValue);
        var expected = new byte[malformedPayload.Length];
        var actual = new byte[malformedPayload.Length];

        Assert.Equal(0, ContainerPageProjection.ProjectPayload(
            registry, live, PropertyVisibility.Everyone, expected));
        Assert.Equal(0, GroundItemPayloadProjection.Project(
            registry, malformedPayload, quarantined: false, identified: true, revealedMask: ulong.MaxValue, actual));

        byte[] malformedWrapper = [0x4B, 0x45, 0x43, 0x51, 0x09];
        ContainerPageChange quarantined = Entry(
            malformedWrapper, quarantined: true, identified: true, revealedMask: ulong.MaxValue);
        Assert.Throws<ArgumentException>(() => ContainerPageProjection.ProjectPayload(
            registry, quarantined, PropertyVisibility.Everyone, new byte[malformedWrapper.Length]));
        Assert.Throws<ArgumentException>(() => GroundItemPayloadProjection.Project(
            registry,
            malformedWrapper,
            quarantined: true,
            identified: true,
            revealedMask: ulong.MaxValue,
            new byte[malformedWrapper.Length]));
    }

    static ContainerPageChange Entry(
        byte[] payload,
        bool quarantined,
        bool identified,
        ulong revealedMask)
        => ContainerPageChange.Occupied(
            new PageSlotInput(
                12,
                quarantined ? ItemContainerPageCodec.EntryFlagQuarantined : 0,
                4200,
                1,
                InstanceIdAllocator.Pack(0, 9_012),
                payload),
            identified,
            revealedMask);
}

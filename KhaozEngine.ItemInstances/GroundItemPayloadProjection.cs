using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The public payload safe to seat on a ground item's replicated component. A ground item has no owner
/// viewer, so this is the stored payload projected at <see cref="PropertyVisibility.Everyone"/> with no
/// owner remainder.
/// <para>
/// This type delegates to <see cref="ContainerPageProjection"/>'s projection core. Container replication
/// and a public ground drop therefore use one visibility rule, one canonical serializer and one quarantine
/// policy without adding an item dependency to tile netcode.
/// </para>
/// </summary>
public static class GroundItemPayloadProjection
{
    /// <summary>Writes the public ground view of one stored payload and returns the bytes written.</summary>
    /// <param name="registry">The property kinds this process knows.</param>
    /// <param name="storedPayload">The full stored payload, or its quarantine wrapper.</param>
    /// <param name="quarantined">Whether <paramref name="storedPayload"/> is a quarantine wrapper.</param>
    /// <param name="identified">Whether the item is identified.</param>
    /// <param name="revealedMask">Kind 128's revealed mask.</param>
    /// <param name="destination">Where the public view is written. A span as long as
    /// <paramref name="storedPayload"/> always suffices.</param>
    /// <returns>
    /// The bytes written. Zero means a plain stack or a live payload that cannot be projected. A quarantined
    /// payload becomes a hollow wrapper carrying its reason and stamp over no preserved bytes.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the stored
    /// payload, or <paramref name="quarantined"/> is true and the stored bytes are not a valid wrapper.</exception>
    public static int Project(
        InstancePropertyRegistry registry,
        ReadOnlySpan<byte> storedPayload,
        bool quarantined,
        bool identified,
        ulong revealedMask,
        Span<byte> destination)
        => ContainerPageProjection.ProjectStoredPayload(
            registry,
            storedPayload,
            quarantined,
            identified,
            revealedMask,
            PropertyVisibility.Everyone,
            destination,
            slot: -1);
}

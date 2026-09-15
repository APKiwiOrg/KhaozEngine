using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The ONE function answering "may this viewer see this field", and the two projections built on it.
/// Contracts 11.2 states the rule and spec 12.5 states it in order, and both the replication filter of
/// spec 7.4 and the tooltip builder call <see cref="CanSee(InstancePropertyRegistry, ushort, PropertyVisibility, bool, ulong)"/>
/// rather than computing their own answer. A tooltip that computed its own is how a client eventually
/// renders something the server never sent.
/// <para>
/// <b>Everything here is pure and static</b>, so a test calls it with no server. The registry arrives as an
/// ARGUMENT rather than an ambient static, because the registry is per instance by design and a projection
/// is sometimes run against one that deliberately omits a kind.
/// </para>
/// <para>
/// <b>A GROUND item has no owner viewer, and that is a rule rather than an omission.</b> A drop's entity is
/// the drop, whose net id is nobody's, so there is no viewer this design calls the owner of a ground stack
/// (spec 7.4). A ground item's public view is therefore
/// <see cref="PublicView"/> at <see cref="PropertyVisibility.Everyone"/>, and there is NO owner remainder
/// for a drop: the sibling component carries the whole of what a passer-by ever receives. The consequence
/// worth stating is the leak that does not happen. Kind 6 <c>BoundTo</c> is <see cref="PropertyVisibility.OwnerOnly"/>,
/// so it is stripped before the component is written and a passer-by cannot read who a dropped item is
/// bound to, which is a fact about a PLAYER rather than about an item. Kinds 4 and 5, charges and
/// durability, go for the same reason, which is why the 58 byte rare of spec 3.8 replicates as 54 bytes on
/// the ground (budget 11 of spec 16). A game that wants a dropper-only view builds it on the claim's own
/// ownership tag and a targeted message, and the engine ships neither in v1.
/// </para>
/// <para>
/// <b>An unregistered kind is visible to nobody.</b> Contracts 11.2 is an if and only if over the kind's
/// registered visibility, and a kind this registry does not hold has none, so there is nothing to compare
/// against and the answer is no. Failing closed is the only safe direction, because a kind this process
/// cannot classify may well be <see cref="PropertyVisibility.ServerOnly"/> in the build that wrote it.
/// That is about a PROJECTION and does not touch contracts 9.4: an unknown kind is still kept verbatim in
/// storage and still survives a decode and rebuild untouched.
/// </para>
/// </summary>
public static class ItemInstanceVisibility
{
    /// <summary>
    /// Whether one viewer may see one field, which is spec 12.5's rule in its order.
    /// <para>
    /// <see cref="PropertyVisibility.ServerOnly"/> is never visible to anyone.
    /// <see cref="PropertyVisibility.OwnerOnly"/> is visible when the viewer level is
    /// <see cref="PropertyVisibility.OwnerOnly"/>. <see cref="PropertyVisibility.Everyone"/> is visible
    /// always. THEN, and only then, the identification gate: a kind carrying an
    /// <see cref="InstancePropertyRegistration.IdentificationMaskBit"/> is hidden while
    /// <paramref name="identified"/> is false and its bit in <paramref name="revealedMask"/> is clear, EVEN
    /// FROM THE OWNER. That last clause is why unidentified is a mechanic rather than a fourth visibility
    /// level, and it is the whole of spec 12.7.
    /// </para>
    /// <para>
    /// A viewer level of <see cref="PropertyVisibility.ServerOnly"/> sees NOTHING. Contracts 11.2 gives a
    /// viewer two levels, <see cref="PropertyVisibility.Everyone"/> normally and
    /// <see cref="PropertyVisibility.OwnerOnly"/> when the viewer owns the item, so
    /// <see cref="PropertyVisibility.ServerOnly"/> is a level a KIND carries and never a viewer's. A caller
    /// passing it has a bug and gets the empty view rather than a privileged one, because the one
    /// privileged reader in this design is the server reading its own stored bytes, which never comes
    /// through here at all.
    /// </para>
    /// </summary>
    /// <param name="kind">The field's registration, which is what the replication filter already holds
    /// after walking the registry.</param>
    /// <param name="viewerLevel">The viewer's clearance for THIS item.</param>
    /// <param name="identified">Whether the item is identified, which is kind 128's state byte.</param>
    /// <param name="revealedMask">Kind 128's revealed mask. It is a <c>ulong</c> because the shape walk
    /// reads a varint at the full 64 bits, so narrowing it would have to happen at some call site
    /// (KhaozEngine issue 917). Bits above
    /// <see cref="InstancePropertyRegistry.MaxIdentificationMaskBit"/> gate nothing, because the registry
    /// refuses to register one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="kind"/> is null.</exception>
    public static bool CanSee(
        InstancePropertyRegistration kind,
        PropertyVisibility viewerLevel,
        bool identified,
        ulong revealedMask)
    {
        ArgumentNullException.ThrowIfNull(kind);

        // 1. ServerOnly never leaves the server, whoever is asking, and it is not a viewer level either.
        if (kind.Visibility == PropertyVisibility.ServerOnly || viewerLevel == PropertyVisibility.ServerOnly)
        {
            return false;
        }

        // 2. OwnerOnly reaches the owner alone. 3. Everyone reaches every viewer. Anything that is neither
        //    a known kind level nor a known viewer level lands here and is refused, which is the same fail
        //    closed direction an unregistered kind takes.
        if (kind.Visibility < viewerLevel || kind.Visibility > PropertyVisibility.Everyone)
        {
            return false;
        }

        // 4. THEN the identification gate, which hides a gated kind from the OWNER too.
        return !kind.IsIdentificationGated
            || identified
            || (revealedMask & (1UL << kind.IdentificationMaskBit)) != 0;
    }

    /// <summary>
    /// The same rule, reached by kind id, which is the door the tooltip builder comes through. An
    /// unregistered kind is visible to nobody.
    /// </summary>
    /// <param name="registry">The property kinds this process knows.</param>
    /// <param name="kind">The property kind id.</param>
    /// <param name="viewerLevel">The viewer's clearance for THIS item.</param>
    /// <param name="identified">Whether the item is identified.</param>
    /// <param name="revealedMask">Kind 128's revealed mask.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    public static bool CanSee(
        InstancePropertyRegistry registry,
        ushort kind,
        PropertyVisibility viewerLevel,
        bool identified,
        ulong revealedMask)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return registry.TryGet(kind, out InstancePropertyRegistration? registration)
            && CanSee(registration, viewerLevel, identified, revealedMask);
    }

    /// <summary>
    /// The payload one viewer may see, written into <paramref name="destination"/>. Returns the bytes
    /// written, or <c>-1</c>.
    /// <para>
    /// It is a FORWARD PASS over the retained runs of the input and nothing more. Fields are already
    /// ascending and each is length prefixed, so a filtered payload is a sequence of copies over contiguous
    /// ranges with no decode into values, no re-sort and no allocation beyond the destination. Spec 3.3
    /// declines contracts 11.2's optional coupling of kind ids to visibility for exactly this reason: the
    /// coupling would buy one copy instead of two or three, forever, in exchange for constraining every
    /// future kind assignment. The levels are deliberately NOT monotonic in the kind id (kinds 4, 5 and 6
    /// are owner-only while 7 and 8 are public), so the run walk is the only correct shape.
    /// </para>
    /// <para>
    /// The output is CANONICAL and decodes, because what is left is still a subsequence of an ascending,
    /// duplicate-free, minimally encoded list. Kind 128 itself is not gated, so an unidentified item's
    /// public view still says it is unidentified, which is what puts <c>khaoz.item.unidentified</c> in the
    /// tooltip rather than a blank line.
    /// </para>
    /// </summary>
    /// <param name="registry">The property kinds this process knows.</param>
    /// <param name="payload">The full payload.</param>
    /// <param name="level">The viewer's clearance for this item.</param>
    /// <param name="identified">Whether the item is identified.</param>
    /// <param name="revealedMask">Kind 128's revealed mask.</param>
    /// <param name="destination">Where the view is written. A destination as long as
    /// <paramref name="payload"/> always suffices, because a view is never longer than what it filters.</param>
    /// <returns>The bytes written, or <c>-1</c> when <paramref name="destination"/> is too short or
    /// <paramref name="payload"/> does not decode. Nothing is written in either case, so a caller that
    /// ignores the answer cannot ship a truncated view. A caller wanting the REASON calls
    /// <see cref="ItemInstancePayload.Validate(InstancePropertyRegistry, ReadOnlySpan{byte})"/>, which is
    /// also why this answers rather than throws: a projection runs inside a tick.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    public static int PublicView(
        InstancePropertyRegistry registry,
        ReadOnlySpan<byte> payload,
        PropertyVisibility level,
        bool identified,
        ulong revealedMask,
        Span<byte> destination)
        => Project(registry, payload, level, identified, revealedMask, complement: false, destination);

    /// <summary>
    /// The owner-only remainder: what the owner of an item sees that its public view does not carry,
    /// written into <paramref name="destination"/>. Returns the bytes written, or <c>-1</c> under the same
    /// two conditions as <see cref="PublicView"/>.
    /// <para>
    /// It is the exact COMPLEMENT of <see cref="PublicView"/> at <see cref="PropertyVisibility.Everyone"/>,
    /// in the same retained-run shape, and it lives here beside it so the two cannot come to disagree. The
    /// public view's bytes plus this remainder's bytes reconstruct everything an identified item's owner
    /// may see, field for field. What neither carries is a <see cref="PropertyVisibility.ServerOnly"/>
    /// field, which never leaves the server, and a gated field whose bit is still clear, which the gate
    /// withholds from the owner too.
    /// </para>
    /// <para>
    /// <b>Spec 7.6 lists the owner remainder as a server-to-client message and names no package to build
    /// its bytes</b>, and spec 7.4 only says it rides a targeted game message. This is that choice made:
    /// the BYTES are the engine's, beside the projection they complement, and the MESSAGE KIND stays the
    /// game's, because <c>TileProtocol</c> reserves the <c>ushort</c> kind space to the game and the engine
    /// only caps the frame.
    /// </para>
    /// </summary>
    /// <param name="registry">The property kinds this process knows.</param>
    /// <param name="payload">The full payload.</param>
    /// <param name="identified">Whether the item is identified. The gate needs it: a gated owner-only
    /// field is withheld from the owner too, so a remainder built without it would hand back exactly what
    /// the gate withheld.</param>
    /// <param name="revealedMask">Kind 128's revealed mask.</param>
    /// <param name="destination">Where the remainder is written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    public static int OwnerRemainder(
        InstancePropertyRegistry registry,
        ReadOnlySpan<byte> payload,
        bool identified,
        ulong revealedMask,
        Span<byte> destination)
        => Project(
            registry,
            payload,
            PropertyVisibility.OwnerOnly,
            identified,
            revealedMask,
            complement: true,
            destination);

    /// <summary>
    /// The one projection both public members are, which is why neither can drift from the other. It walks
    /// the decoded field positions twice: once to keep what the viewer may see and total it, and once to
    /// copy the contiguous runs that survived.
    /// </summary>
    static int Project(
        InstancePropertyRegistry registry,
        ReadOnlySpan<byte> payload,
        PropertyVisibility level,
        bool identified,
        ulong revealedMask,
        bool complement,
        Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(registry);

        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(registry, payload, fields, out int count, out _))
        {
            return -1;
        }

        // Compact the kept fields over the front of the same span, so each kind is looked up ONCE and the
        // run walk below reads only what survived.
        int kept = 0;
        int needed = 0;
        for (int index = 0; index < count; index++)
        {
            PayloadField field = fields[index];
            if (!Keep(registry, field.Kind, level, identified, revealedMask, complement))
            {
                continue;
            }

            fields[kept++] = field;
            needed += field.FieldLength;
        }

        if (destination.Length < needed)
        {
            return -1;
        }

        int written = 0;
        int runStart = -1;
        int runEnd = -1;
        for (int index = 0; index < kept; index++)
        {
            PayloadField field = fields[index];

            // Fields are contiguous in the payload, so two kept fields are one run exactly when nothing
            // was dropped between them.
            if (runStart >= 0 && field.FieldStart != runEnd)
            {
                written += Flush(payload, destination, written, ref runStart, ref runEnd);
            }

            if (runStart < 0)
            {
                runStart = field.FieldStart;
            }

            runEnd = field.BodyStart + field.BodyLength;
        }

        return written + Flush(payload, destination, written, ref runStart, ref runEnd);
    }

    /// <summary>
    /// Whether one field belongs in this projection, which is one
    /// <see cref="CanSee(InstancePropertyRegistration, PropertyVisibility, bool, ulong)"/> and, for the
    /// remainder, two. Writing the complement as the two calls rather than as a shortcut on the registered
    /// level is what makes "there is one function" literally true here.
    /// </summary>
    static bool Keep(
        InstancePropertyRegistry registry,
        ushort kind,
        PropertyVisibility level,
        bool identified,
        ulong revealedMask,
        bool complement)
    {
        if (!registry.TryGet(kind, out InstancePropertyRegistration? registration))
        {
            return false;
        }

        if (!CanSee(registration, level, identified, revealedMask))
        {
            return false;
        }

        return !complement || !CanSee(registration, PropertyVisibility.Everyone, identified, revealedMask);
    }

    /// <summary>Copies one retained run and resets it, answering how many bytes it moved.</summary>
    static int Flush(
        ReadOnlySpan<byte> payload,
        Span<byte> destination,
        int written,
        ref int runStart,
        ref int runEnd)
    {
        if (runStart < 0)
        {
            return 0;
        }

        int length = runEnd - runStart;
        payload.Slice(runStart, length).CopyTo(destination[written..]);
        runStart = -1;
        runEnd = -1;
        return length;
    }
}

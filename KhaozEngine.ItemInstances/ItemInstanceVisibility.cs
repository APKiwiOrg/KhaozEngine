using System;
using KhaozEngine.Catalog;

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
    /// It is a FORWARD PASS over the retained runs of the input, except at a field that CARRIES a nested
    /// payload. Fields are already ascending and each is length prefixed, so a filtered payload is a
    /// sequence of copies over contiguous ranges with no decode into values and no re-sort. Spec 3.3
    /// declines contracts 11.2's optional coupling of kind ids to visibility for exactly this reason: the
    /// coupling would buy one copy instead of two or three, forever, in exchange for constraining every
    /// future kind assignment. The levels are deliberately NOT monotonic in the kind id (kinds 4, 5 and 6
    /// are owner-only while 7 and 8 are public), so the run walk is the only correct shape.
    /// </para>
    /// <para>
    /// <b>A field whose registered shape nests is REBUILT rather than copied.</b> Kind 132 is
    /// <see cref="PropertyVisibility.Everyone"/>, so copying it whole shipped the gem inside it exactly as
    /// stored, and a socketed gem's kind 5 and kind 6 reached every viewer including a passer-by reading a
    /// ground stack. Contracts 11.2 is an if and only if over the kind's own visibility and it does not stop
    /// at the first level, so each nested payload is projected through this same function at the same viewer
    /// level, and the socket entry's length varint, the entry run and the field's own length are recomputed
    /// innermost first. One level is the whole of it, because contracts 9.5 allows no second. Nothing
    /// allocates beyond the destination either way: the rebuild's working buffer is a
    /// <c>stackalloc</c> capped at the payload cap, and the walk runs twice, once to total what the viewer
    /// may see and once to write it, so a rebuilt field's length is known before a byte is written.
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
    /// <b>The complement runs one level down too.</b> A socket's FRAME is public, so the remainder carries a
    /// copy of it only to position the owner-only fields of the gem inside, and a socket holding none of
    /// them leaves no frame behind at all, which is what keeps a ground stack's remainder empty. A field the
    /// public view drops whole is the other case: it owes the owner everything in it rather than a
    /// difference, so its nested payloads are projected at <see cref="PropertyVisibility.OwnerOnly"/>
    /// without complementing.
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
    /// the payload twice: once to total what the viewer may see, and once to write it. The two passes are
    /// what make a rebuilt field's own length varint knowable before anything is written, and they are why
    /// a destination that is too short answers <c>-1</c> with nothing written at all.
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

        var lens = new Lens(level, identified, revealedMask, complement, Depth: 0);
        int needed = Walk(registry, payload, lens, default, measure: true);
        if (needed < 0 || destination.Length < needed)
        {
            return -1;
        }

        return Walk(registry, payload, lens, destination, measure: false);
    }

    /// <summary>
    /// One projection of one payload, answering the bytes written, or the bytes it WOULD write when
    /// <paramref name="measure"/> is set, or <c>-1</c>.
    /// <para>
    /// A field the viewer keeps whole is copied as part of a contiguous run, which is what the fields being
    /// ascending and length prefixed buys. A field that CARRIES a nested payload is rebuilt instead, because
    /// its own visibility says nothing about what is inside it.
    /// </para>
    /// </summary>
    static int Walk(
        InstancePropertyRegistry registry,
        ReadOnlySpan<byte> payload,
        in Lens lens,
        Span<byte> destination,
        bool measure)
    {
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(registry, payload, fields, out int count, out _))
        {
            return -1;
        }

        // Where a rebuilt field's body is built before its length is known. A projected body is never
        // longer than the body it projects, so the cap is the whole of what it can need.
        Span<byte> rebuilt = stackalloc byte[ItemInstancePayload.MaxInstancePayloadBytes];

        int written = 0;
        int runStart = -1;
        int runEnd = -1;
        for (int index = 0; index < count; index++)
        {
            PayloadField field = fields[index];
            KeptField kept = Classify(
                registry,
                field.Kind,
                lens,
                out InstancePropertyRegistration? registration,
                out Lens inner);

            if (kept == KeptField.Dropped)
            {
                continue;
            }

            if (kept == KeptField.Copied)
            {
                // Fields are contiguous in the payload, so two copied fields are one run exactly when
                // nothing was dropped or rebuilt between them.
                if (runStart >= 0 && field.FieldStart != runEnd)
                {
                    written += Flush(payload, destination, written, measure, ref runStart, ref runEnd);
                }

                if (runStart < 0)
                {
                    runStart = field.FieldStart;
                }

                runEnd = field.BodyStart + field.BodyLength;
                continue;
            }

            int body = Rebuild(
                registry,
                registration!.Shape,
                payload.Slice(field.BodyStart, field.BodyLength),
                inner,
                rebuilt,
                out int nestedBytes);

            if (body < 0)
            {
                return -1;
            }

            // The remainder carries a PUBLIC field's frame only to position the owner-only bytes inside
            // it, so a socket holding none of them leaves no frame behind and a ground stack's remainder
            // stays empty.
            if (lens.Complement && inner.Complement && nestedBytes == 0)
            {
                continue;
            }

            written += Flush(payload, destination, written, measure, ref runStart, ref runEnd);
            if (!measure)
            {
                int at = written;
                at += ContentVarint.Write(destination[at..], field.Kind);
                at += ContentVarint.Write(destination[at..], (uint)body);
                rebuilt[..body].CopyTo(destination[at..]);
            }

            written += ContentVarint.Size(field.Kind) + ContentVarint.Size((uint)body) + body;
        }

        return written + Flush(payload, destination, written, measure, ref runStart, ref runEnd);
    }

    /// <summary>
    /// What one projection does with one field, which is one
    /// <see cref="CanSee(InstancePropertyRegistration, PropertyVisibility, bool, ulong)"/> and, for the
    /// remainder, two. Writing the complement as the two calls rather than as a shortcut on the registered
    /// level is what makes "there is one function" literally true here.
    /// <para>
    /// <paramref name="inner"/> is the lens the field's NESTED payloads are projected through. For the
    /// public view it is this same viewer one level down. For the remainder it is the complement inside a
    /// field the public view already carries, and the plain view inside a field the public view drops,
    /// because a dropped field owes the owner everything in it rather than a difference.
    /// </para>
    /// </summary>
    static KeptField Classify(
        InstancePropertyRegistry registry,
        ushort kind,
        in Lens lens,
        out InstancePropertyRegistration? registration,
        out Lens inner)
    {
        inner = lens;
        if (!registry.TryGet(kind, out registration)
            || !CanSee(registration, lens.Level, lens.Identified, lens.RevealedMask))
        {
            return KeptField.Dropped;
        }

        // The one level limit of contracts 9.5, derived from the shape rather than from kind 132, so a game
        // kind that declares a nesting slot is projected at both levels too.
        bool nests = lens.Depth == 0 && registration.Shape.Nests;
        inner = lens with { Depth = lens.Depth + 1 };

        if (!lens.Complement)
        {
            return nests ? KeptField.Rebuilt : KeptField.Copied;
        }

        if (!CanSee(registration, PropertyVisibility.Everyone, lens.Identified, lens.RevealedMask))
        {
            inner = inner with { Complement = false };
            return nests ? KeptField.Rebuilt : KeptField.Copied;
        }

        return nests ? KeptField.Rebuilt : KeptField.Dropped;
    }

    /// <summary>
    /// Rebuilds one field's body against its registered shape, projecting each nested payload it carries.
    /// Answers the bytes written into <paramref name="destination"/>, or <c>-1</c>.
    /// <para>
    /// <b>Innermost first, as the remap pass is.</b> A nested payload's projected bytes are final before the
    /// varint declaring their length is written, and that length is final before the field length above it.
    /// Nothing here patches a byte in place.
    /// </para>
    /// </summary>
    /// <param name="registry">The property kinds this process knows.</param>
    /// <param name="shape">The field's registered shape, which is what says where its nested payloads sit.</param>
    /// <param name="body">The field's bytes, without its kind and length prefix.</param>
    /// <param name="inner">The lens the nested payloads are projected through.</param>
    /// <param name="destination">Where the rebuilt body goes.</param>
    /// <param name="nestedBytes">How many bytes the nested payloads contributed, which is what says whether
    /// a remainder's copy of a public field's frame is carrying anything at all.</param>
    static int Rebuild(
        InstancePropertyRegistry registry,
        InstanceFieldShape shape,
        ReadOnlySpan<byte> body,
        in Lens inner,
        Span<byte> destination,
        out int nestedBytes)
    {
        nestedBytes = 0;
        int offset = 0;
        int written = 0;
        if (!RebuildRun(registry, shape.Header.Span, body, ref offset, inner, destination, ref written, ref nestedBytes))
        {
            return -1;
        }

        if (shape.Count != InstanceCountWidth.None)
        {
            // The repeat count does not change, so its own bytes are copied rather than re-encoded and the
            // width the shape declares cannot be narrowed by accident.
            int countStart = offset;
            if (!ReadCount(shape.Count, body, ref offset, out uint count)
                || !Copy(body[countStart..offset], destination, ref written))
            {
                return -1;
            }

            ReadOnlySpan<InstanceSlotKind> entry = shape.Entry.Span;
            for (uint repeat = 0; repeat < count; repeat++)
            {
                if (!RebuildRun(registry, entry, body, ref offset, inner, destination, ref written, ref nestedBytes))
                {
                    return -1;
                }
            }
        }

        // Bytes left over after the shape is spent are a field that does not match its own shape, which the
        // decode already refused. Answering -1 is the defensive half.
        return offset == body.Length ? written : -1;
    }

    /// <summary>
    /// Copies one run of slots, projecting a nested payload as it meets one. Every other slot's bytes go
    /// across verbatim, because a projection changes what a payload CARRIES and never what a value says.
    /// </summary>
    static bool RebuildRun(
        InstancePropertyRegistry registry,
        ReadOnlySpan<InstanceSlotKind> slots,
        ReadOnlySpan<byte> body,
        ref int offset,
        in Lens inner,
        Span<byte> destination,
        ref int written,
        ref int nestedBytes)
    {
        foreach (InstanceSlotKind slot in slots)
        {
            int start = offset;
            switch (slot)
            {
                case InstanceSlotKind.Varint:
                    if (!ContentVarint.TryReadUInt64(body, ref offset, out _, out _))
                    {
                        return false;
                    }

                    break;

                case InstanceSlotKind.Byte:
                    if (offset >= body.Length)
                    {
                        return false;
                    }

                    offset++;
                    break;

                case InstanceSlotKind.Fixed2:
                    if (body.Length - offset < 2)
                    {
                        return false;
                    }

                    offset += 2;
                    break;

                case InstanceSlotKind.NestedPayload:
                    if (!ContentVarint.TryRead(body, ref offset, out uint length, out _)
                        || length > (uint)(body.Length - offset))
                    {
                        return false;
                    }

                    ReadOnlySpan<byte> nested = body.Slice(offset, (int)length);
                    offset += (int)length;

                    int projected = Walk(registry, nested, inner, default, measure: true);
                    if (projected < 0
                        || destination.Length - written < ContentVarint.Size((uint)projected) + projected)
                    {
                        return false;
                    }

                    written += ContentVarint.Write(destination[written..], (uint)projected);
                    if (Walk(registry, nested, inner, destination[written..], measure: false) != projected)
                    {
                        return false;
                    }

                    written += projected;
                    nestedBytes += projected;
                    continue;

                default:
                    return false;
            }

            if (!Copy(body[start..offset], destination, ref written))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Copies one slot's bytes across unchanged, answering false when they do not fit.</summary>
    static bool Copy(ReadOnlySpan<byte> slot, Span<byte> destination, ref int written)
    {
        if (destination.Length - written < slot.Length)
        {
            return false;
        }

        slot.CopyTo(destination[written..]);
        written += slot.Length;
        return true;
    }

    /// <summary>Reads a field's repeat count at the width its shape declares, which is a BYTE for an affix
    /// list and a VARINT for a socket list.</summary>
    static bool ReadCount(InstanceCountWidth width, ReadOnlySpan<byte> body, ref int offset, out uint count)
    {
        switch (width)
        {
            case InstanceCountWidth.Byte:
                if (offset >= body.Length)
                {
                    count = 0;
                    return false;
                }

                count = body[offset++];
                return true;

            case InstanceCountWidth.Varint:
                return ContentVarint.TryRead(body, ref offset, out count, out _);

            default:
                count = 0;
                return false;
        }
    }

    /// <summary>Copies one retained run and resets it, answering how many bytes it moved.</summary>
    static int Flush(
        ReadOnlySpan<byte> payload,
        Span<byte> destination,
        int written,
        bool measure,
        ref int runStart,
        ref int runEnd)
    {
        if (runStart < 0)
        {
            return 0;
        }

        int length = runEnd - runStart;
        if (!measure)
        {
            payload.Slice(runStart, length).CopyTo(destination[written..]);
        }

        runStart = -1;
        runEnd = -1;
        return length;
    }

    /// <summary>What a projection does with one field.</summary>
    enum KeptField : byte
    {
        /// <summary>The viewer sees nothing of it.</summary>
        Dropped = 0,

        /// <summary>Its bytes go across verbatim, as part of a contiguous run.</summary>
        Copied = 1,

        /// <summary>It carries a nested payload, so it is rebuilt around the projection of each one.</summary>
        Rebuilt = 2,
    }

    /// <summary>
    /// The viewer, the half and the depth one walk reads through, so the four travel together rather than
    /// as four parameters that a nested call could pass in the wrong order.
    /// </summary>
    /// <param name="Level">The viewer's clearance for this item.</param>
    /// <param name="Identified">Whether the item is identified.</param>
    /// <param name="RevealedMask">Kind 128's revealed mask.</param>
    /// <param name="Complement">Whether this is the owner remainder rather than the view itself.</param>
    /// <param name="Depth">0 for the payload itself, 1 for a payload inside a nesting slot. Contracts 9.5
    /// allows no third level, and the decoder has already refused one.</param>
    readonly record struct Lens(
        PropertyVisibility Level,
        bool Identified,
        ulong RevealedMask,
        bool Complement,
        int Depth);
}

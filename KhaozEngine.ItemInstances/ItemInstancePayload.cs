using System;
using KhaozEngine.Catalog;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// One decoded field's POSITION in the payload it came from, which is all a decode ever hands back. The
/// bytes stay where they were, so an unknown kind is kept verbatim by construction (contracts 9.4) and
/// nothing on this path copies a body to read it.
/// </summary>
/// <param name="Kind">The property kind id, contracts 9.2.</param>
/// <param name="BodyStart">Where the field's bytes begin, past its kind and length varints.</param>
/// <param name="BodyLength">How many bytes the field declared.</param>
public readonly record struct PayloadField(ushort Kind, int BodyStart, int BodyLength)
{
    /// <summary>Where the WHOLE field begins, at its kind varint.</summary>
    public int FieldStart => BodyStart - ContentVarint.Size(Kind) - ContentVarint.Size((uint)BodyLength);

    /// <summary>The whole field's byte count, kind varint and length varint included.</summary>
    public int FieldLength => BodyStart + BodyLength - FieldStart;
}

/// <summary>
/// The tagged field sequence of contracts 9.1 and spec 3.2, which is everything an owned item carries
/// beyond its definition id, its count and its instance id.
/// <para>
/// <c>[Kind: varint uint16][Length: varint int32][Bytes: Length bytes]</c>, repeated zero or more times. No
/// header, no magic and no version byte, because a payload never travels alone: it rides inside a container
/// page, a ground item component or a journal event, each of which carries its own version. An EMPTY
/// payload is zero bytes, which is what a plain stack has.
/// </para>
/// <para>
/// <b>The ENCODER is what makes the payload canonical</b> and the decoder CHECKS it: fields strictly
/// ascending by kind, no kind twice, every varint minimal (contracts 9.3). Together those make one set of
/// properties into exactly one byte sequence, which is what turns the stacking rule of spec 4.6 into a
/// <see cref="SequenceEqual"/> instead of a structural comparison that has to decode both sides.
/// </para>
/// <para>
/// <b>The decoder NEVER throws for a byte.</b> It answers false plus one of the eight tokens of
/// <see cref="InstancePayloadReason"/>, following <c>ItemContainerCodec.TryDecode</c>, because the bytes
/// arrive from a remote peer or a stored page. What DOES throw is a caller error: a destination span that
/// cannot hold the answer, or an item built over the cap.
/// </para>
/// </summary>
public static partial class ItemInstancePayload
{
    /// <summary>
    /// The largest payload a non-quarantined item may carry, contracts 9.6. The same number the slot
    /// carries rather than a second copy of it, so the two cannot drift.
    /// <para>
    /// It moves ONE WAY: raising it is backward compatible because every existing payload is still legal,
    /// and lowering it strands items already over it with no remap rule that could rescue them.
    /// </para>
    /// </summary>
    public const int MaxInstancePayloadBytes = ItemSlot.MaxPayloadBytes;

    /// <summary>
    /// The most fields a legal payload can hold, which is the cap over the smallest possible field: a one
    /// byte kind varint plus a one byte length varint plus an empty body. A caller sizing a
    /// <see cref="PayloadField"/> span for <see cref="TryDecode(InstancePropertyRegistry, ReadOnlySpan{byte}, Span{PayloadField}, out int, out string)"/>
    /// uses this, and a shorter span is refused at the door rather than silently truncating the answer.
    /// </summary>
    public const int MaxFields = MaxInstancePayloadBytes / 2;

    /// <summary>
    /// Decodes a payload against a registry, which is the FULL check: the three canonical rules, the cap,
    /// every declared length, each known kind's registered shape, each known kind's own codec, and the one
    /// level nesting limit.
    /// </summary>
    /// <param name="registry">The property kinds this decoder knows. A kind it does not hold is kept
    /// verbatim and never inspected, which is contracts 9.4.</param>
    /// <param name="payload">The bytes, which may be empty.</param>
    /// <param name="fields">Where the decoded field positions are written, at least
    /// <see cref="MaxFields"/> long.</param>
    /// <param name="fieldCount">How many fields the payload held.</param>
    /// <param name="reason">The closed-set refusal token, null when the answer is true.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fields"/> is shorter than
    /// <see cref="MaxFields"/>. That is a caller error rather than a fact about the bytes, which is why it
    /// throws while nothing about the bytes ever does.</exception>
    public static bool TryDecode(
        InstancePropertyRegistry registry,
        ReadOnlySpan<byte> payload,
        Span<PayloadField> fields,
        out int fieldCount,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Walk(registry, payload, fields, nested: false, out fieldCount, out reason);
    }

    /// <summary>
    /// Decodes a payload with NO registry, which checks the STRUCTURE alone: the three canonical rules of
    /// contracts 9.3, the cap, and every declared length lying inside the payload.
    /// <para>
    /// It treats every kind as unknown, so it never reads a field body. The per-kind shape, the per-kind
    /// codec and the one level nesting limit all need a registered shape to check against and are the
    /// sibling overload's. Use this only where structure is genuinely the question.
    /// </para>
    /// </summary>
    /// <param name="payload">The bytes, which may be empty.</param>
    /// <param name="fields">Where the decoded field positions are written, at least
    /// <see cref="MaxFields"/> long.</param>
    /// <param name="fieldCount">How many fields the payload held.</param>
    /// <param name="reason">The closed-set refusal token, null when the answer is true.</param>
    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        Span<PayloadField> fields,
        out int fieldCount,
        out string? reason)
        => Walk(registry: null, payload, fields, nested: false, out fieldCount, out reason);

    /// <summary>
    /// The refusal token a full decode would answer, or null when the payload is fine. This is spec 12.2's
    /// checks 1 to 5 as ONE call, which is the surface the instance validator uses rather than writing a
    /// second copy of them.
    /// </summary>
    /// <param name="registry">The property kinds this decoder knows.</param>
    /// <param name="payload">The bytes, which may be empty.</param>
    public static string? Validate(InstancePropertyRegistry registry, ReadOnlySpan<byte> payload)
    {
        Span<PayloadField> fields = stackalloc PayloadField[MaxFields];
        TryDecode(registry, payload, fields, out _, out string? reason);
        return reason;
    }

    /// <summary>
    /// The refusal token a STRUCTURAL decode would answer, or null. Checks what the registry-free
    /// <see cref="TryDecode(ReadOnlySpan{byte}, Span{PayloadField}, out int, out string)"/> checks and no
    /// more.
    /// </summary>
    /// <param name="payload">The bytes, which may be empty.</param>
    public static string? Validate(ReadOnlySpan<byte> payload)
    {
        Span<PayloadField> fields = stackalloc PayloadField[MaxFields];
        TryDecode(payload, fields, out _, out string? reason);
        return reason;
    }

    /// <summary>Whether a payload passes the full check of <see cref="Validate(InstancePropertyRegistry, ReadOnlySpan{byte})"/>.</summary>
    /// <param name="registry">The property kinds this decoder knows.</param>
    /// <param name="payload">The bytes, which may be empty.</param>
    public static bool IsCanonical(InstancePropertyRegistry registry, ReadOnlyMemory<byte> payload)
        => Validate(registry, payload.Span) is null;

    /// <summary>
    /// Whether a payload is canonical in the STRUCTURAL sense of contracts 9.3, which is the container's
    /// door check: <c>ItemContainer</c> takes it as a <c>Func&lt;ReadOnlyMemory&lt;byte&gt;, bool&gt;</c>
    /// because <c>KhaozEngine.Items</c> sits BELOW this package and cannot call into it (contracts 3.2),
    /// and the delegate shape is why this overload carries no registry.
    /// <para>
    /// <b>Structural means structural.</b> It treats every kind as unknown, so it never reads a field body
    /// and never recurses into a nested payload: a socket carrying nested bytes that are themselves out of
    /// order passes here and is caught by the registry overload. A host that wants the per-kind checks at
    /// the container door passes a registry-bound predicate instead, for example
    /// <c>p =&gt; ItemInstancePayload.Validate(registry, p.Span) is null</c>.
    /// </para>
    /// </summary>
    /// <param name="payload">The bytes, which may be empty.</param>
    public static bool IsCanonical(ReadOnlyMemory<byte> payload) => Validate(payload.Span) is null;

    /// <summary>
    /// Whether two payloads hold the same properties, which is a byte compare because the encoding is
    /// canonical. Spec 4.6's stacking rule is this call and nothing anywhere decodes two payloads to
    /// compare them.
    /// </summary>
    /// <param name="left">One payload.</param>
    /// <param name="right">The other.</param>
    public static bool SequenceEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        => left.SequenceEqual(right);

    /// <summary>
    /// Writes a builder's fields out canonically: ascending by kind, no kind twice, every varint minimal.
    /// Returns the bytes written.
    /// </summary>
    /// <param name="builder">The fields to write, which the builder already holds in ascending order.</param>
    /// <param name="destination">Where to write, at least <see cref="ItemInstancePayloadBuilder.Length"/>
    /// long.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    /// <exception cref="InvalidOperationException">The fields total more than
    /// <see cref="MaxInstancePayloadBytes"/>. Spec 4.7 makes an over-cap item a throw at the door rather
    /// than a refusal token, because it is an item that must not be WRITTEN rather than bytes that were
    /// read.</exception>
    public static int Encode(ItemInstancePayloadBuilder builder, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(builder);

        int length = builder.Length;
        CheckCap(length);

        if (destination.Length < length)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"The payload needs {length} bytes and the destination holds {destination.Length}."),
                nameof(destination));
        }

        int written = 0;
        foreach (PayloadFieldBytes field in builder.Fields)
        {
            written += ContentVarint.Write(destination[written..], field.Kind);
            written += ContentVarint.Write(destination[written..], (uint)field.Body.Length);
            field.Body.Span.CopyTo(destination[written..]);
            written += field.Body.Length;
        }

        return written;
    }

    /// <summary>
    /// The payload a viewer at one clearance may see. Returns the bytes written, or <c>-1</c> when
    /// <paramref name="destination"/> is too short.
    /// <para>
    /// <b>This is a STUB that returns the whole payload</b>, and it is deliberate rather than forgotten.
    /// The visibility rule of spec 12.5, the one <c>CanSee</c> function and the forward pass over retained
    /// runs, ships with the visibility task of the phase 2-3 plan, which is where spec 20 puts it. The
    /// member exists here because the container and the codec are written against it now.
    /// </para>
    /// </summary>
    /// <param name="payload">The full payload.</param>
    /// <param name="level">The viewer's clearance, unused until the visibility task lands.</param>
    /// <param name="identified">Whether the item is identified, unused until then.</param>
    /// <param name="revealedMask">Kind 128's revealed mask, unused until then.</param>
    /// <param name="destination">Where to write the view.</param>
    public static int PublicView(
        ReadOnlySpan<byte> payload,
        PropertyVisibility level,
        bool identified,
        uint revealedMask,
        Span<byte> destination)
    {
        _ = level;
        _ = identified;
        _ = revealedMask;

        if (destination.Length < payload.Length)
        {
            return -1;
        }

        payload.CopyTo(destination);
        return payload.Length;
    }

    /// <summary>
    /// The cap rule, in ONE place, so the encoder and the builder cannot come to differ about it.
    /// </summary>
    internal static void CheckCap(int length)
    {
        if (length <= MaxInstancePayloadBytes)
        {
            return;
        }

        throw new InvalidOperationException(FormattableString.Invariant(
            $"The payload would be {length} bytes and the cap is {MaxInstancePayloadBytes} (contracts 9.6). What the cap DECIDES is how deep a socketed gem may be, which is an authored publish-time number rather than a refusal at the moment a player clicks socket."));
    }

    /// <summary>
    /// The one field walk, used by every entry point above and by the nested payload inside a socket.
    /// </summary>
    static bool Walk(
        InstancePropertyRegistry? registry,
        ReadOnlySpan<byte> payload,
        Span<PayloadField> fields,
        bool nested,
        out int fieldCount,
        out string? reason)
    {
        if (fields.Length < MaxFields)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"A decode writes up to {MaxFields} fields and the span holds {fields.Length}. Sizing it shorter would silently truncate the answer on a payload nobody expected to be dense."),
                nameof(fields));
        }

        fieldCount = 0;
        reason = null;

        if (payload.Length > MaxInstancePayloadBytes)
        {
            reason = InstancePayloadReason.PayloadTooLong;
            return false;
        }

        int offset = 0;
        long previousKind = -1;
        while (offset < payload.Length)
        {
            if (!ContentVarint.TryRead(payload, ref offset, out uint rawKind, out reason))
            {
                return false;
            }

            // Contracts 9.2 reserves kind 0 and stops at 65535, so neither is an unknown kind to preserve.
            if (rawKind == 0 || rawKind > ushort.MaxValue)
            {
                reason = InstancePayloadReason.FieldMalformed;
                return false;
            }

            if (rawKind == previousKind)
            {
                reason = InstancePayloadReason.KindDuplicate;
                return false;
            }

            if (rawKind < previousKind)
            {
                reason = InstancePayloadReason.KindOutOfOrder;
                return false;
            }

            previousKind = rawKind;

            if (!ContentVarint.TryRead(payload, ref offset, out uint rawLength, out reason))
            {
                return false;
            }

            if (rawLength > (uint)(payload.Length - offset))
            {
                reason = InstancePayloadReason.FieldTruncated;
                return false;
            }

            var kind = (ushort)rawKind;
            int bodyLength = (int)rawLength;

            if (registry is not null
                && !CheckBody(registry, kind, payload.Slice(offset, bodyLength), nested, out reason))
            {
                return false;
            }

            fields[fieldCount++] = new PayloadField(kind, offset, bodyLength);
            offset += bodyLength;
        }

        return true;
    }
}

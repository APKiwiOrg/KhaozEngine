using System;
using System.Buffers.Binary;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The re-encode half of the pass: one payload, one field, one run of slots, and the canonical order a list
/// that declares one gets back afterwards.
/// <para>
/// <b>Innermost first, by construction.</b> A nesting slot is rewritten while the run around it is being
/// READ, so the nested payload's bytes are final before the slot that declares their length is written, and
/// that length is final before the field length above it, and the field length before the payload the entry
/// carries. Nothing here patches a byte in place.
/// </para>
/// <para>
/// <b>Every refusal is an ABANDON rather than a throw.</b> The bytes arrive from a stored page, so a rewrite
/// that cannot be expressed answers <see cref="Abandon"/> and the entry keeps the bytes it had. That covers a
/// payload that does not decode, a replacement id too wide for the slot that holds it (kind 130's rarity id
/// is a BYTE and there is no wider form of the field to widen into), and a result that would break the
/// payload's own canonical form.
/// </para>
/// </summary>
public static partial class InstanceRemapPass
{
    /// <summary>The buffers ONE level of the walk owns, so no level can write into another's.</summary>
    readonly ref struct RemapLevel
    {
        public RemapLevel(
            Span<byte> body,
            Span<PayloadField> fields,
            Span<ulong> values,
            Span<byte> arena,
            Span<int> nested)
        {
            Body = body;
            Fields = fields;
            Values = values;
            Arena = arena;
            Nested = nested;
        }

        /// <summary>Where one field's rewritten body is built before its length is known.</summary>
        public Span<byte> Body { get; }

        /// <summary>This level's decoded field positions.</summary>
        public Span<PayloadField> Fields { get; }

        /// <summary>This level's slot values.</summary>
        public Span<ulong> Values { get; }

        /// <summary>Where a nested payload's rewritten bytes wait until the run around them is written. Empty
        /// at the deeper level, which can carry no nesting field at all (contracts 9.5).</summary>
        public Span<byte> Arena { get; }

        /// <summary>Where in <see cref="Arena"/> each nesting slot's bytes begin.</summary>
        public Span<int> Nested { get; }
    }

    /// <summary>
    /// One walk over one entry's payload, holding the registry, the resolver and both levels' buffers.
    /// </summary>
    readonly ref struct RemapWalk
    {
        readonly InstancePropertyRegistry _properties;
        readonly RemapResolver _resolver;
        readonly RemapLevel _outer;
        readonly RemapLevel _inner;
        readonly Span<byte> _sorted;
        readonly Span<int> _starts;
        readonly Span<int> _lengths;
        readonly Span<long> _keys;

        public RemapWalk(
            InstancePropertyRegistry properties,
            RemapResolver resolver,
            in RemapLevel outer,
            in RemapLevel inner,
            Span<byte> sorted,
            Span<int> starts,
            Span<int> lengths,
            Span<long> keys)
        {
            _properties = properties;
            _resolver = resolver;
            _outer = outer;
            _inner = inner;
            _sorted = sorted;
            _starts = starts;
            _lengths = lengths;
            _keys = keys;
        }

        /// <summary>
        /// Rewrites one payload and answers the bytes written, or <see cref="Abandon"/>. An UNREGISTERED kind
        /// and a registered kind carrying no reference and no nesting slot are copied verbatim, which is
        /// contracts 9.4 and what lets a client built against content build N re-save a field only build N
        /// plus one knows.
        /// </summary>
        /// <param name="source">The payload as the page holds it, already known to decode.</param>
        /// <param name="destination">Where the rewritten payload goes.</param>
        /// <param name="level">0 for the entry's own payload, 1 for a payload inside a nesting slot.</param>
        public int RewritePayload(ReadOnlySpan<byte> source, Span<byte> destination, int level)
        {
            RemapLevel buffers = level == 0 ? _outer : _inner;
            if (!ItemInstancePayload.TryDecode(_properties, source, buffers.Fields, out int count, out _))
            {
                return Abandon;
            }

            int written = 0;
            for (int index = 0; index < count; index++)
            {
                PayloadField field = buffers.Fields[index];
                ReadOnlySpan<byte> body = source.Slice(field.BodyStart, field.BodyLength);

                if (_properties.TryGet(field.Kind, out InstancePropertyRegistration? registration)
                    && (!registration.References.IsEmpty || registration.Shape.Nests))
                {
                    int length = RewriteField(body, registration, buffers.Body, level);
                    if (length < 0) return Abandon;

                    body = buffers.Body[..length];
                }

                int size = ContentVarint.Size(field.Kind) + ContentVarint.Size((uint)body.Length) + body.Length;
                if (destination.Length - written < size) return Abandon;

                written += ContentVarint.Write(destination[written..], field.Kind);
                written += ContentVarint.Write(destination[written..], (uint)body.Length);
                body.CopyTo(destination[written..]);
                written += body.Length;
            }

            return written;
        }

        /// <summary>
        /// Rewrites ONE field's body against its registered shape: the header slots, the repeat count as the
        /// shape writes it, and one run per repeat, with the canonical order restored afterwards on a list
        /// whose codec declares one.
        /// </summary>
        int RewriteField(
            ReadOnlySpan<byte> body,
            InstancePropertyRegistration registration,
            Span<byte> destination,
            int level)
        {
            RemapLevel buffers = level == 0 ? _outer : _inner;
            InstanceFieldShape shape = registration.Shape;
            int slots = Math.Max(shape.Header.Length, shape.Entry.Length);
            Span<ulong> values = slots <= buffers.Values.Length ? buffers.Values : new ulong[slots];
            Span<int> nested = slots <= buffers.Nested.Length ? buffers.Nested : new int[slots];

            int offset = 0;
            int written = 0;
            int used = 0;
            if (!ReadRun(shape.Header.Span, body, ref offset, values, nested, ref used, level)) return Abandon;

            ApplyTargets(registration, InstanceReferenceSite.Header, values);
            int run = WriteRun(shape.Header.Span, values, nested, buffers.Arena, destination[written..]);
            if (run < 0) return Abandon;

            written += run;
            if (shape.Count == InstanceCountWidth.None) return written;

            if (!ReadCount(shape.Count, body, ref offset, out uint count)) return Abandon;

            // A list whose codec is the shipped affix list is held ASCENDING by its first entry reference
            // (contracts 9.9), and a replacement can move an id past its neighbour. Reading that off the
            // registered CODEC rather than off kinds 131 and 133 is what gives a game kind adopting the same
            // codec the same re-sort. A nesting list is never re-sorted: its entries are authored order by
            // construction, and buffering them would mean two levels sharing one arena.
            bool sorted = ReferenceEquals(registration.Codec, InstancePropertyCodec.AffixList) && !shape.Nests;
            ReadOnlySpan<InstanceSlotKind> entry = shape.Entry.Span;

            if (!sorted)
            {
                int width = WriteCount(shape.Count, count, destination[written..]);
                if (width < 0) return Abandon;

                written += width;
                for (uint repeat = 0; repeat < count; repeat++)
                {
                    used = 0;
                    if (!ReadRun(entry, body, ref offset, values, nested, ref used, level)) return Abandon;

                    ApplyTargets(registration, InstanceReferenceSite.Entry, values);
                    run = WriteRun(entry, values, nested, buffers.Arena, destination[written..]);
                    if (run < 0) return Abandon;

                    written += run;
                }

                return offset == body.Length ? written : Abandon;
            }

            if (count > (uint)_starts.Length) return Abandon;

            int fill = 0;
            for (uint repeat = 0; repeat < count; repeat++)
            {
                used = 0;
                if (!ReadRun(entry, body, ref offset, values, nested, ref used, level)) return Abandon;

                ApplyTargets(registration, InstanceReferenceSite.Entry, values);
                run = WriteRun(entry, values, nested, buffers.Arena, _sorted[fill..]);
                if (run < 0) return Abandon;

                _starts[(int)repeat] = fill;
                _lengths[(int)repeat] = run;
                _keys[(int)repeat] = SortKey(registration, values);
                fill += run;
            }

            if (offset != body.Length) return Abandon;

            Sort((int)count);
            int countWidth = WriteCount(shape.Count, count, destination[written..]);
            if (countWidth < 0) return Abandon;

            written += countWidth;
            for (int repeat = 0; repeat < (int)count; repeat++)
            {
                ReadOnlySpan<byte> bytes = _sorted.Slice(_starts[repeat], _lengths[repeat]);
                if (destination.Length - written < bytes.Length) return Abandon;

                bytes.CopyTo(destination[written..]);
                written += bytes.Length;
            }

            return written;
        }

        /// <summary>
        /// Reads one run of slots, recursing into a nesting slot as it meets it, which is what makes the
        /// rewrite innermost first and what puts this walk in the same order as the validator's.
        /// </summary>
        bool ReadRun(
            ReadOnlySpan<InstanceSlotKind> slots,
            ReadOnlySpan<byte> body,
            ref int offset,
            Span<ulong> values,
            Span<int> nested,
            ref int used,
            int level)
        {
            for (int index = 0; index < slots.Length; index++)
            {
                switch (slots[index])
                {
                    case InstanceSlotKind.Varint:
                        // At the FULL unsigned 64 bit width, because the shape declares a value's position
                        // and not its width: a socket's contained instance id is a uint64 and a node
                        // prefixed one sets the high bit.
                        if (!ContentVarint.TryReadUInt64(body, ref offset, out ulong value, out _)) return false;

                        values[index] = value;
                        break;

                    case InstanceSlotKind.Byte:
                        if (offset >= body.Length) return false;

                        values[index] = body[offset++];
                        break;

                    case InstanceSlotKind.Fixed2:
                        if (body.Length - offset < 2) return false;

                        values[index] = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
                        offset += 2;
                        break;

                    case InstanceSlotKind.NestedPayload:
                        // The one level limit of contracts 9.5, which the decoder already refused at: this is
                        // the defensive half, so the walk cannot recurse without bound whatever it is handed.
                        if (level != 0) return false;

                        if (!ContentVarint.TryRead(body, ref offset, out uint length, out _)
                            || length > (uint)(body.Length - offset))
                        {
                            return false;
                        }

                        int written = RewritePayload(body.Slice(offset, (int)length), _outer.Arena[used..], level + 1);
                        if (written < 0) return false;

                        nested[index] = used;
                        values[index] = (ulong)written;
                        used += written;
                        offset += (int)length;
                        break;

                    default:
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Writes one run back from the values the rules left. A value too wide for the slot that holds it is
        /// refused rather than truncated: there is no wider form of a byte slot to widen into.
        /// </summary>
        static int WriteRun(
            ReadOnlySpan<InstanceSlotKind> slots,
            ReadOnlySpan<ulong> values,
            ReadOnlySpan<int> nested,
            ReadOnlySpan<byte> arena,
            Span<byte> destination)
        {
            int written = 0;
            for (int index = 0; index < slots.Length; index++)
            {
                ulong value = values[index];
                switch (slots[index])
                {
                    case InstanceSlotKind.Varint:
                        if (destination.Length - written < ContentVarint.SizeUInt64(value)) return Abandon;

                        written += ContentVarint.WriteUInt64(destination[written..], value);
                        break;

                    case InstanceSlotKind.Byte:
                        if (value > byte.MaxValue || destination.Length - written < 1) return Abandon;

                        destination[written++] = (byte)value;
                        break;

                    case InstanceSlotKind.Fixed2:
                        if (value > ushort.MaxValue || destination.Length - written < 2) return Abandon;

                        BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], (ushort)value);
                        written += 2;
                        break;

                    case InstanceSlotKind.NestedPayload:
                        int length = (int)value;
                        if (destination.Length - written < ContentVarint.Size((uint)length) + length) return Abandon;

                        written += ContentVarint.Write(destination[written..], (uint)length);
                        arena.Slice(nested[index], length).CopyTo(destination[written..]);
                        written += length;
                        break;

                    default:
                        return Abandon;
                }
            }

            return written;
        }

        /// <summary>
        /// Resolves the ids one run holds, in the order the registration declares its targets. An id of 0 is
        /// SKIPPED, because contracts 5.1 never assigns 0 and it is how a payload spells absent. A value wider
        /// than an int is skipped too: a rule carries an int32 id, so no rule can name one.
        /// </summary>
        void ApplyTargets(
            InstancePropertyRegistration registration,
            InstanceReferenceSite site,
            Span<ulong> values)
        {
            foreach (InstanceReferenceTarget target in registration.References.Span)
            {
                if (target.Site != site || target.SlotIndex >= values.Length) continue;

                ulong raw = values[target.SlotIndex];
                if (raw == 0 || raw > int.MaxValue) continue;

                int toId = _resolver.Resolve(target.ContentTypeKey, (int)raw);
                values[target.SlotIndex] = (ulong)toId;
            }
        }

        /// <summary>The value a re-sorted list orders by, which is its first ENTRY reference target.</summary>
        static long SortKey(InstancePropertyRegistration registration, ReadOnlySpan<ulong> values)
        {
            foreach (InstanceReferenceTarget target in registration.References.Span)
            {
                if (target.Site == InstanceReferenceSite.Entry && target.SlotIndex < values.Length)
                {
                    return (long)Math.Min(values[target.SlotIndex], long.MaxValue);
                }
            }

            return 0;
        }

        /// <summary>
        /// A STABLE insertion sort over the built entries, so two entries a rule left sharing a key keep the
        /// order they arrived in rather than an order that depends on the sort.
        /// </summary>
        void Sort(int count)
        {
            for (int outer = 1; outer < count; outer++)
            {
                int start = _starts[outer];
                int length = _lengths[outer];
                long key = _keys[outer];
                int inner = outer - 1;
                while (inner >= 0 && _keys[inner] > key)
                {
                    _starts[inner + 1] = _starts[inner];
                    _lengths[inner + 1] = _lengths[inner];
                    _keys[inner + 1] = _keys[inner];
                    inner--;
                }

                _starts[inner + 1] = start;
                _lengths[inner + 1] = length;
                _keys[inner + 1] = key;
            }
        }

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

        /// <summary>Writes the count back at the width the shape declares, which is a BYTE for an affix list
        /// and a VARINT for a socket list, and narrowing either would be a format change.</summary>
        static int WriteCount(InstanceCountWidth width, uint count, Span<byte> destination)
        {
            switch (width)
            {
                case InstanceCountWidth.Byte:
                    if (count > byte.MaxValue || destination.IsEmpty) return Abandon;

                    destination[0] = (byte)count;
                    return 1;

                case InstanceCountWidth.Varint:
                    if (destination.Length < ContentVarint.Size(count)) return Abandon;

                    return ContentVarint.Write(destination, count);

                default:
                    return Abandon;
            }
        }
    }
}

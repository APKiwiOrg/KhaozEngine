using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.ItemInstances.Payload;

/// <summary>
/// The six ways a golden is corrupted, each its own theory case so a red run names which one moved rather
/// than "the fuzzer failed".
/// </summary>
public enum MutationClass
{
    /// <summary>One bit of one byte, anywhere, which is the corruption a disk or a wire actually produces.</summary>
    BitFlip,

    /// <summary>The payload cut short, which is the truncated page and the short read.</summary>
    Truncate,

    /// <summary>A field's declared length changed while its bytes are not, which is the hostile shape.</summary>
    LengthLie,

    /// <summary>A field's kind changed, so a body meets a shape that was never written for it.</summary>
    KindSwap,

    /// <summary>A repeating field's count changed, so the walk is told to read more entries than exist.</summary>
    CountLie,

    /// <summary>Kind 132 nested around the golden, which is spec 15.6's decompression bomb.</summary>
    NestDeeper,
}

/// <summary>
/// Mutation over a checked-in golden, which is the whole corpus strategy of spec 17's fuzzing note. Random
/// bytes reject at the first varint and prove nothing. A mutant of a VALID payload gets past the door and
/// exercises the shape walk, the per-kind codecs and the nesting limit, which is where a real corruption
/// lands.
/// <para>
/// Every draw comes from the <see cref="IRandomSource"/> it was handed, so a mutator built on a
/// <see cref="SeededRandomSource"/> with a constant seed produces the same sequence of mutants forever.
/// Nothing here reads the clock, and nothing here decides whether a mutant is legal: several classes
/// produce a payload that is still perfectly valid, which is a fine outcome and is the fraction the fuzz
/// test reports.
/// </para>
/// </summary>
public sealed class PayloadMutator
{
    readonly byte[] _source;
    readonly MutationClass _class;
    readonly IRandomSource _random;
    readonly PayloadField[] _fields;
    readonly int _fieldCount;
    readonly InstancePropertyRegistry _registry = InstancePropertyRegistry.CreateV1();

    /// <summary>Builds a mutator over one golden.</summary>
    /// <param name="source">The golden's bytes, which are never modified in place.</param>
    /// <param name="mutation">Which corruption to produce.</param>
    /// <param name="random">The draws. A seeded source makes the whole sequence reproducible.</param>
    public PayloadMutator(ReadOnlySpan<byte> source, MutationClass mutation, IRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _source = source.ToArray();
        _class = mutation;
        _random = random;

        // The field layout of the ORIGINAL, read once: the length, kind and count classes all aim at a
        // position the golden itself declared, which is what makes them targeted rather than another bit
        // flip. A golden always decodes, so a failure here is a broken golden rather than a broken mutator.
        _fields = new PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(_source, _fields, out _fieldCount, out string? reason))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"the fuzz corpus holds a payload that does not decode: {reason}"),
                nameof(source));
        }
    }

    /// <summary>The next mutant, as a fresh array.</summary>
    public byte[] Next() => _class switch
    {
        MutationClass.BitFlip => BitFlip(),
        MutationClass.Truncate => Truncate(),
        MutationClass.LengthLie => FlipValueBit(LengthVarintEnd()),
        MutationClass.KindSwap => FlipValueBit(KindVarintEnd()),
        MutationClass.CountLie => CountLie(),
        MutationClass.NestDeeper => NestDeeper(),
        _ => throw new ArgumentOutOfRangeException(nameof(_class), _class, "No mutation class by that name."),
    };

    /// <summary>One bit of one byte. An empty golden has no byte to flip and answers itself.</summary>
    byte[] BitFlip()
    {
        byte[] mutant = (byte[])_source.Clone();
        if (mutant.Length == 0)
        {
            return mutant;
        }

        int index = _random.NextInt(0, mutant.Length);
        mutant[index] ^= (byte)(1 << _random.NextInt(0, 8));
        return mutant;
    }

    /// <summary>
    /// The payload cut at a random point BEFORE its end, so the mutant is always shorter. A cut that lands
    /// exactly on a field boundary produces a valid shorter payload, which is why the accepted fraction of
    /// this class is not zero and should not be.
    /// </summary>
    byte[] Truncate()
    {
        if (_source.Length == 0)
        {
            return Array.Empty<byte>();
        }

        return _source[.._random.NextInt(0, _source.Length)];
    }

    /// <summary>
    /// Flips one VALUE bit of the varint ending at <paramref name="end"/>, which is the byte before the
    /// position the caller names. Bits 0 to 6 carry value and bit 7 is the continuation flag, so the width
    /// of the varint is preserved and the mutation is a lie about the number rather than a reshaped field.
    /// A negative position means the golden had no field to aim at, and the class degrades to a bit flip
    /// rather than returning the input untouched.
    /// </summary>
    byte[] FlipValueBit(int end)
    {
        if (end < 0)
        {
            return BitFlip();
        }

        byte[] mutant = (byte[])_source.Clone();
        mutant[end] ^= (byte)(1 << _random.NextInt(0, 7));
        return mutant;
    }

    /// <summary>The last byte of a random field's LENGTH varint, or -1 when there are no fields.</summary>
    int LengthVarintEnd()
    {
        if (_fieldCount == 0)
        {
            return -1;
        }

        return _fields[_random.NextInt(0, _fieldCount)].BodyStart - 1;
    }

    /// <summary>The last byte of a random field's KIND varint, or -1 when there are no fields.</summary>
    int KindVarintEnd()
    {
        if (_fieldCount == 0)
        {
            return -1;
        }

        PayloadField field = _fields[_random.NextInt(0, _fieldCount)];
        return field.FieldStart + ContentVarint.Size(field.Kind) - 1;
    }

    /// <summary>
    /// A repeating field's count, told to be something else. The candidates are DERIVED from the registry:
    /// a kind whose shape declares a count and no header slots writes that count as its first body byte,
    /// which is a byte for kind 131 and a single-byte varint for kind 132 at any count a golden holds. The
    /// replacement stays under <c>0x80</c> so it is legal as either width, and the lie is in the value.
    /// </summary>
    byte[] CountLie()
    {
        List<int> candidates = new();
        for (int index = 0; index < _fieldCount; index++)
        {
            PayloadField field = _fields[index];
            if (field.BodyLength > 0
                && _registry.TryGet(field.Kind, out InstancePropertyRegistration? registration)
                && registration.Shape.Count != InstanceCountWidth.None
                && registration.Shape.Header.IsEmpty)
            {
                candidates.Add(field.BodyStart);
            }
        }

        if (candidates.Count == 0)
        {
            return BitFlip();
        }

        byte[] mutant = (byte[])_source.Clone();
        int position = candidates[_random.NextInt(0, candidates.Count)];
        mutant[position] = (byte)((mutant[position] + _random.NextInt(1, 8)) & 0x7F);
        return mutant;
    }

    /// <summary>
    /// The golden wrapped in two to four levels of kind 132, which is the decompression bomb of spec 15.6:
    /// a hundred bytes asking a decoder to recurse. The one level limit makes it a refusal at a constant
    /// depth instead.
    /// <para>
    /// A golden too large to wrap is nested around an EMPTY leaf, so the mutant stays inside
    /// <see cref="ItemInstancePayload.MaxInstancePayloadBytes"/> and the class keeps testing nesting rather
    /// than the cap, which the bit flip and length classes already reach.
    /// </para>
    /// </summary>
    byte[] NestDeeper()
    {
        const int leafBudget = 256;
        byte[] payload = _source.Length <= leafBudget ? _source : Array.Empty<byte>();

        int depth = _random.NextInt(2, 5);
        for (int level = 0; level < depth; level++)
        {
            payload = new ItemInstancePayloadBuilder()
                .AddSockets(new[] { new InstanceSocket(7, 833, 4201, payload) })
                .ToArray();
        }

        return payload;
    }
}

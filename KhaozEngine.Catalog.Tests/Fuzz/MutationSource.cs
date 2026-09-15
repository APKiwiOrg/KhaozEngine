using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Catalog;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Catalog.Fuzz;

/// <summary>The six mutations of spec 15.2, one per kind of damage a hostile or broken producer does.</summary>
internal enum GoldenMutation
{
    /// <summary>Flip one bit, the cheapest way to reach a field's own validation.</summary>
    BitFlip = 0,

    /// <summary>Cut the file at a random offset, which is what reaches the two length refusals.</summary>
    Truncate = 1,

    /// <summary>Zero a run, which reaches the orderings and the duplicate checks.</summary>
    ZeroRun = 2,

    /// <summary>Splice a prefix of one golden onto a suffix of another, so a header describes another body.</summary>
    Splice = 3,

    /// <summary>Set a byte's varint continuation bit, which reaches the minimality and overflow refusals.</summary>
    VarintContinuation = 4,

    /// <summary>Set the container's reserved field, the one extension a reader may not skip.</summary>
    ReservedSet = 5,
}

/// <summary>One mutated file and enough about it to reproduce the case from the printed seed.</summary>
/// <param name="Kind">Which mutation produced it.</param>
/// <param name="Where">The offset the mutation touched, or -1 where the mutation has no single offset.</param>
/// <param name="Bytes">The mutant.</param>
internal sealed record GoldenMutant(GoldenMutation Kind, int Where, byte[] Bytes)
{
    /// <summary>A one line description for an assertion message.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Kind} at {Where}, {Bytes.Length} bytes");
}

/// <summary>
/// The mutation fuzzer's source of damage, spec 15.2. It is SEEDED through
/// <see cref="SeededRandomSource"/>, which is the engine's one deterministic stream, so every failure
/// reproduces from the seed the assertion prints and nothing here reads an ambient static.
/// <para>
/// It holds no process-global state and takes its randomness as a constructor parameter (contracts 14.4),
/// so a fuzz class needs no <c>DisableParallelization</c> collection.
/// </para>
/// </summary>
internal sealed class MutationSource
{
    /// <summary>How many mutations there are, which is what the uniform pick is taken over.</summary>
    public const int MutationCount = 6;

    /// <summary>The longest run <see cref="GoldenMutation.ZeroRun"/> zeroes, so a mutant stays a near miss.</summary>
    public const int MaxZeroRun = 32;

    readonly IRandomSource _random;
    readonly IReadOnlyList<byte[]> _corpus;

    /// <summary>Builds a source over a corpus, which is what the splice mutation draws its second file from.</summary>
    /// <param name="random">The seeded stream every choice is drawn from.</param>
    /// <param name="corpus">Every golden of the version being fuzzed, the seed file included.</param>
    public MutationSource(IRandomSource random, IReadOnlyList<byte[]> corpus)
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(corpus);
        if (corpus.Count == 0)
        {
            throw new ArgumentException("A mutation source needs at least one file to splice against.", nameof(corpus));
        }

        _random = random;
        _corpus = corpus;
    }

    /// <summary>Produces one mutant of <paramref name="seed"/>, choosing a mutation uniformly.</summary>
    public GoldenMutant Next(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var kind = (GoldenMutation)_random.NextInt(0, MutationCount);
        return kind switch
        {
            GoldenMutation.BitFlip => BitFlip(seed),
            GoldenMutation.Truncate => Truncate(seed),
            GoldenMutation.ZeroRun => ZeroRun(seed),
            GoldenMutation.Splice => Splice(seed),
            GoldenMutation.VarintContinuation => Continuation(seed),
            _ => Reserved(seed),
        };
    }

    GoldenMutant BitFlip(byte[] seed)
    {
        if (seed.Length == 0)
        {
            return new GoldenMutant(GoldenMutation.BitFlip, -1, []);
        }

        byte[] bytes = (byte[])seed.Clone();
        int at = _random.NextInt(0, bytes.Length);
        bytes[at] ^= (byte)(1 << _random.NextInt(0, 8));
        return new GoldenMutant(GoldenMutation.BitFlip, at, bytes);
    }

    GoldenMutant Truncate(byte[] seed)
    {
        int at = seed.Length == 0 ? 0 : _random.NextInt(0, seed.Length);
        return new GoldenMutant(GoldenMutation.Truncate, at, seed[..at]);
    }

    GoldenMutant ZeroRun(byte[] seed)
    {
        if (seed.Length == 0)
        {
            return new GoldenMutant(GoldenMutation.ZeroRun, -1, []);
        }

        byte[] bytes = (byte[])seed.Clone();
        int at = _random.NextInt(0, bytes.Length);
        int run = _random.NextInt(1, Math.Min(bytes.Length - at, MaxZeroRun) + 1);
        bytes.AsSpan(at, run).Clear();
        return new GoldenMutant(GoldenMutation.ZeroRun, at, bytes);
    }

    GoldenMutant Splice(byte[] seed)
    {
        byte[] other = _corpus[_random.NextInt(0, _corpus.Count)];
        int head = _random.NextInt(0, seed.Length + 1);
        int tail = _random.NextInt(0, other.Length + 1);
        byte[] bytes = new byte[head + (other.Length - tail)];
        seed.AsSpan(0, head).CopyTo(bytes);
        other.AsSpan(tail).CopyTo(bytes.AsSpan(head));
        return new GoldenMutant(GoldenMutation.Splice, head, bytes);
    }

    GoldenMutant Continuation(byte[] seed)
    {
        // Past the magic, because a varint never appears in one and the magic has its own refusal.
        if (seed.Length <= ContentPackFormat.MagicBytes)
        {
            return BitFlip(seed) with { Kind = GoldenMutation.VarintContinuation };
        }

        byte[] bytes = (byte[])seed.Clone();
        int at = _random.NextInt(ContentPackFormat.MagicBytes, bytes.Length);
        bytes[at] |= 0x80;
        return new GoldenMutant(GoldenMutation.VarintContinuation, at, bytes);
    }

    GoldenMutant Reserved(byte[] seed)
    {
        int at = ReservedOffset(seed);
        if (at < 0 || at >= seed.Length)
        {
            return BitFlip(seed) with { Kind = GoldenMutation.ReservedSet };
        }

        byte[] bytes = (byte[])seed.Clone();
        bytes[at] = (byte)_random.NextInt(1, 256);
        return new GoldenMutant(GoldenMutation.ReservedSet, at, bytes);
    }

    /// <summary>
    /// Where each container puts the reserved field a reader refuses on: 26 in a <c>KECC</c> header, 7 in a
    /// <c>KECM</c> and a <c>KECR</c>, and <c>8 + tagLen</c> in the variable <c>KECT</c> header. Returns -1 for
    /// bytes whose magic names no format, which a splice or a truncate produces.
    /// </summary>
    static int ReservedOffset(ReadOnlySpan<byte> file)
    {
        if (file.Length < ContentPackFormat.MagicBytes)
        {
            return -1;
        }

        ReadOnlySpan<byte> magic = file[..ContentPackFormat.MagicBytes];
        if (magic.SequenceEqual(ContentPackFormat.ChunkMagic)) return 26;
        if (magic.SequenceEqual(ContentPackFormat.ManifestMagic)) return 7;
        if (magic.SequenceEqual(ContentPackFormat.RuleChunkMagic)) return 7;
        if (magic.SequenceEqual(ContentPackFormat.TextChunkMagic)) return file.Length > 6 ? 8 + file[6] : -1;
        return -1;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Catalog.Goldens;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Fuzz;

/// <summary>
/// The decoder fuzzer of spec 15.2: mutate the goldens and hold the decoders to three invariants.
/// <para>
/// <b>It never throws.</b> Every decode entry point answers false plus a reason, because the bytes arrive
/// from a remote peer and a decoder is total. <b>Reasons are stable.</b> Every refusal carries a token from
/// the accepted set, asserted as MEMBERSHIP rather than as a specific token per mutant, because a bit flip
/// legitimately turns one failure into another. <b>A mutant that decodes must round trip</b>, re-encoding to
/// its own canonical bytes, which is what catches a decoder that silently normalises a difference away and
/// so stops a canonical format being canonical.
/// </para>
/// <para>
/// <b>The accepted set is REFLECTED, not hand written.</b> Spec 15.2's fixed list names thirteen tokens and
/// is incomplete: every decoder in the package declares its full token set as <c>public const string</c>
/// members, so the set here is every <c>Reason*</c> constant in <c>KhaozEngine.Catalog</c> MINUS
/// <see cref="ContentPackReader"/>'s own, which are the FETCH outcomes a mutation can never produce because
/// the fuzzer mutates bytes the test already holds. A token outside that set is a failure, and
/// <see cref="TheAcceptedSet_CoversTheFixedListOfSpecFifteenTwo"/> pins the spec's thirteen inside it.
/// </para>
/// <para>
/// 10,000 mutants per golden per run, a few seconds. <c>KE_CATALOG_FUZZ_ITERATIONS</c> is the soak knob and
/// <c>KE_CATALOG_FUZZ_SEED</c> re-runs one seed, so a red case reproduces from the two numbers its message
/// prints. The default seed is FIXED, so CI runs the same 60,000 mutants every time rather than discovering
/// a new red on an unrelated push.
/// </para>
/// </summary>
public sealed class DecoderFuzzTests
{
    /// <summary>The seed every run uses unless <c>KE_CATALOG_FUZZ_SEED</c> names another.</summary>
    public const ulong DefaultSeed = 0x4B45_4343_5345_4544;

    /// <summary>Mutants per golden per run, spec 15.2's number.</summary>
    public const int DefaultIterations = 10_000;

    static readonly IReadOnlySet<string> AcceptedReasons = BuildAcceptedReasons();

    /// <summary>One case per golden of every version directory present.</summary>
    public static TheoryData<string, string> Cases() => GoldenLibrary.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Mutants_NeverThrow_CarryAListedReason_AndRoundTripWhenTheyDecode(string version, string name)
    {
        GoldenFile golden = GoldenLibrary.Get(version, name);
        ContentManifestSide side = SideOf(golden);
        var random = new SeededRandomSource(Seed());
        var source = new MutationSource(random, Corpus(version));

        int iterations = Iterations();
        int decoded = 0;
        for (int i = 0; i < iterations; i++)
        {
            GoldenMutant mutant = source.Next(golden.Stored);
            GoldenRoundTrip trip;
            try
            {
                trip = GoldenCodecs.RoundTrip(mutant.Bytes, side);
            }
            catch (Exception ex)
            {
                Assert.Fail(Where(golden, i, mutant) + " threw " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (!trip.Decoded)
            {
                Assert.True(
                    trip.Reason is not null && AcceptedReasons.Contains(trip.Reason),
                    Where(golden, i, mutant) + " refused with '" + trip.Reason + "', which no decoder declares.");
                continue;
            }

            decoded++;
            Assert.True(trip.Canonical is not null && trip.ReEncoded is not null, Where(golden, i, mutant));
            Assert.True(
                ((ReadOnlySpan<byte>)trip.Canonical).SequenceEqual(trip.ReEncoded),
                Where(golden, i, mutant) + " decoded and re-encoded to different bytes, so the decoder normalised something away.");
        }

        Assert.True(
            decoded > 0,
            Where(golden, iterations, null) + " produced no mutant that decoded at all, so the round trip invariant went unexercised.");
    }

    [Fact]
    public void TheAcceptedSet_CoversTheFixedListOfSpecFifteenTwo()
    {
        // Spec 15.2's fixed list, written out so the reflected set is checked against the spec rather than
        // against itself. The list is a SUBSET: every decoder declares tokens beyond it, which is why the set
        // the fuzzer accepts is reflected instead of copied from here.
        string[] fixedList =
        [
            "chunk-format-version", "chunk-reserved-set", "chunk-range-mismatch", "chunk-too-large",
            "chunk-stored-length", "chunk-row-duplicate", "chunk-row-order", "chunk-row-flags",
            "manifest-wrong-side", "manifest-chunk-slots", "rule-kind", "rule-sequence-gap",
            "rule-sequence-order",
        ];

        var missing = new List<string>();
        foreach (string token in fixedList)
        {
            if (!AcceptedReasons.Contains(token))
            {
                missing.Add(token);
            }
        }

        Assert.True(
            missing.Count == 0,
            "Spec 15.2 names a decode reason no decoder declares as a const any more: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheFetchOutcomes_AreOutsideTheAcceptedSet()
    {
        // Contracts 9.7 and spec 15.2: a transfer outcome is not a decode outcome, and the fuzzer never
        // produces one because it mutates bytes the test already holds.
        Assert.DoesNotContain(ContentPackReader.ReasonHashMismatch, AcceptedReasons);
        Assert.DoesNotContain(ContentPackReader.ReasonManifestHashMismatch, AcceptedReasons);
        Assert.DoesNotContain(ContentPackReader.ReasonFetchFailed, AcceptedReasons);
        Assert.DoesNotContain(ContentPackReader.ReasonTypeUnregistered, AcceptedReasons);
    }

    [Fact]
    public void EveryMutation_ProducesSomething_AndTheSeedReproducesIt()
    {
        byte[][] corpus = Corpus(GoldenLibrary.Versions[0]);
        var first = new MutationSource(new SeededRandomSource(DefaultSeed), corpus);
        var second = new MutationSource(new SeededRandomSource(DefaultSeed), corpus);
        var seen = new HashSet<GoldenMutation>();

        for (int i = 0; i < 600; i++)
        {
            GoldenMutant left = first.Next(corpus[0]);
            GoldenMutant right = second.Next(corpus[0]);
            Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(left.Bytes, right.Bytes);
            seen.Add(left.Kind);
        }

        Assert.Equal(MutationSource.MutationCount, seen.Count);
    }

    static string Where(GoldenFile golden, int iteration, GoldenMutant? mutant) => string.Create(
        CultureInfo.InvariantCulture,
        $"seed {Seed()} golden {golden} mutant {iteration} [{mutant?.Describe() ?? "none"}]");

    static ContentManifestSide SideOf(GoldenFile golden)
        => golden.Kind == GoldenCodecs.ManifestKind
            && golden.Values.GetProperty("manifest").GetProperty("side").GetString() == nameof(ContentManifestSide.Client)
            ? ContentManifestSide.Client
            : ContentManifestSide.Server;

    static byte[][] Corpus(string version)
    {
        IReadOnlyList<GoldenFile> files = GoldenLibrary.Of(version);
        var corpus = new byte[files.Count][];
        for (int i = 0; i < corpus.Length; i++)
        {
            corpus[i] = files[i].Stored;
        }

        return corpus;
    }

    static int Iterations()
        => int.TryParse(
            Environment.GetEnvironmentVariable("KE_CATALOG_FUZZ_ITERATIONS"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int iterations) && iterations > 0
            ? iterations
            : DefaultIterations;

    static ulong Seed()
        => ulong.TryParse(
            Environment.GetEnvironmentVariable("KE_CATALOG_FUZZ_SEED"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out ulong seed)
            ? seed
            : DefaultSeed;

    static IReadOnlySet<string> BuildAcceptedReasons()
    {
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        foreach (Type type in typeof(ContentChunkCodec).Assembly.GetExportedTypes())
        {
            accepted.UnionWith(ReasonsOf(type));
        }

        accepted.ExceptWith(ReasonsOf(typeof(ContentPackReader)));
        return accepted;
    }

    static IEnumerable<string> ReasonsOf(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (field is { IsLiteral: true, IsInitOnly: false }
                && field.FieldType == typeof(string)
                && field.Name.StartsWith("Reason", StringComparison.Ordinal)
                && field.GetRawConstantValue() is string token)
            {
                yield return token;
            }
        }
    }
}

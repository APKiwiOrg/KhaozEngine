using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.ItemInstances.Payload;

/// <summary>
/// Spec 17 row 3, the decoder fuzzer, plus spec 15.6's nesting fence. The corpus is MUTATION OVER THE
/// CHECKED-IN GOLDENS and never random bytes: random bytes reject at the first varint and prove nothing,
/// while a mutation of a valid golden reaches the paths a real corruption reaches.
/// <para>
/// The assertion is a TRIPLE, on every mutant, through both decoder overloads. No throw. A reason from the
/// closed set of contracts 9.7. The SAME reason for the same bytes. A mutant that stays valid is a fine
/// outcome and is counted rather than asserted away, and the accepted fraction is written to the test
/// output per class so a class that stopped producing refusals is visible rather than silent.
/// </para>
/// <para>
/// The corpus is DISCOVERED by listing <c>Payload/Goldens</c>, so a golden added by a later task widens the
/// fuzzer with no change here. The seed is a constant and never the clock: a fuzzer whose corpus changes
/// per run is a flake generator, and a failure prints the seed, the class, the golden and the mutant index,
/// which is everything needed to reproduce it.
/// </para>
/// <para>
/// Every fact builds its own registry, so nothing here writes process-global state.
/// </para>
/// </summary>
public class PayloadFuzzTests
{
    /// <summary>
    /// The one seed, constant and printed on every failure. Contracts 14.2's
    /// <see cref="SeededRandomSource"/> wraps the engine's one deterministic stream, so this number plus
    /// the class, the golden and the mutant index reproduce a failing mutant exactly.
    /// </summary>
    const int Seed = 0x5CA1AB1E;

    /// <summary>Mutants per class per golden. The whole theory stays well under a second in Release.</summary>
    const int MutantsPerGolden = 300;

    readonly ITestOutputHelper _output;

    public PayloadFuzzTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(MutationClass.BitFlip)]
    [InlineData(MutationClass.Truncate)]
    [InlineData(MutationClass.LengthLie)]
    [InlineData(MutationClass.KindSwap)]
    [InlineData(MutationClass.CountLie)]
    [InlineData(MutationClass.NestDeeper)]
    public void Mutating_a_golden_never_throws_and_answers_a_stable_closed_reason(MutationClass kind)
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        IReadOnlyList<string> corpus = Corpus();
        Assert.NotEmpty(corpus);

        int total = 0;
        int accepted = 0;
        foreach (string path in corpus)
        {
            string name = Path.GetFileName(path);
            byte[] source = File.ReadAllBytes(path);
            var mutator = new PayloadMutator(source, kind, new SeededRandomSource(Seed));

            for (int index = 0; index < MutantsPerGolden; index++)
            {
                byte[] mutant = mutator.Next();
                total++;

                // The registry-free overload checks structure alone and the registry one walks every
                // registered shape, so both are fuzzed: a body the shape walk reads is a whole surface the
                // structural walk never touches.
                Answer(mutant, registry: null, kind, name, index);
                if (Answer(mutant, registry, kind, name, index) is null)
                {
                    accepted++;
                }
            }
        }

        _output.WriteLine(FormattableString.Invariant(
            $"{kind}: {accepted} of {total} mutants stayed valid ({(accepted * 100.0 / total).ToString("F1", CultureInfo.InvariantCulture)} percent accepted), seed 0x{Seed:X8}"));

        // A class that refuses NOTHING has stopped testing the decoder, which is the failure mode a fuzzer
        // hides best: it still runs, still passes, and covers nothing.
        Assert.True(
            accepted < total,
            FormattableString.Invariant(
                $"the {kind} class produced {total} mutants and every one stayed valid, so it is no longer mutating anything (seed 0x{Seed:X8})"));
    }

    [Fact]
    public void The_same_seed_produces_the_same_mutants_on_every_run()
    {
        // The fuzzer's own determinism, which is what makes the seed in a failure message worth printing.
        foreach (string path in Corpus())
        {
            byte[] source = File.ReadAllBytes(path);
            foreach (MutationClass kind in Enum.GetValues<MutationClass>())
            {
                var first = new PayloadMutator(source, kind, new SeededRandomSource(Seed));
                var second = new PayloadMutator(source, kind, new SeededRandomSource(Seed));
                for (int index = 0; index < 32; index++)
                {
                    Assert.Equal(first.Next(), second.Next());
                }
            }
        }
    }

    [Fact]
    public void A_payload_nesting_kind_132_three_deep_answers_socket_nesting()
    {
        // Spec 15.6's denial of service fence. The ONE LEVEL limit is structural rather than a budget, so
        // the decoder refuses at the first nested socket rather than recursing to the bottom of the chain.
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();

        byte[] oneLevel = Nest(depth: 1);
        Assert.Null(ItemInstancePayload.Validate(registry, oneLevel));

        byte[] threeDeep = Nest(depth: 3);
        Assert.Equal(InstancePayloadReason.SocketNesting, ItemInstancePayload.Validate(registry, threeDeep));

        // Two levels is already the refusal, which pins WHERE the boundary sits rather than only that one
        // exists. The whole of the depth-3 chain below that point is never read.
        Assert.Equal(
            InstancePayloadReason.SocketNesting,
            ItemInstancePayload.Validate(registry, Nest(depth: 2)));

        // The deepest chain the 512 byte cap holds answers the same token and RETURNS, which is the
        // bounded-stack half: a decoder that recursed per level would go as deep as the payload said.
        byte[] deepest = Nest(depth: 30);
        Assert.True(deepest.Length <= ItemInstancePayload.MaxInstancePayloadBytes);
        Assert.Equal(InstancePayloadReason.SocketNesting, ItemInstancePayload.Validate(registry, deepest));

        // The structural overload never reads a body, so it sees a well formed payload at every depth. The
        // nesting limit is DERIVED from the registered shape and belongs to the registry overload alone.
        Assert.Null(ItemInstancePayload.Validate(threeDeep));
    }

    /// <summary>
    /// Runs one decode, proves it did not throw, and answers its reason token. Both the throw check and the
    /// stability check happen here, so every call site gets the whole triple.
    /// </summary>
    static string? Answer(
        byte[] mutant,
        InstancePropertyRegistry? registry,
        MutationClass kind,
        string golden,
        int index)
    {
        string? reason = null;
        Exception? thrown = Record.Exception(() => reason = registry is null
            ? ItemInstancePayload.Validate(mutant)
            : ItemInstancePayload.Validate(registry, mutant));

        Assert.True(thrown is null, Reproduce(kind, golden, index, mutant) + " threw " + thrown);

        if (reason is not null)
        {
            Assert.True(
                InstancePayloadReason.All.Contains(reason),
                Reproduce(kind, golden, index, mutant) + " answered the open reason '" + reason + "'");
        }

        string? again = registry is null
            ? ItemInstancePayload.Validate(mutant)
            : ItemInstancePayload.Validate(registry, mutant);
        Assert.True(
            reason == again,
            Reproduce(kind, golden, index, mutant) + " answered '" + reason + "' then '" + again + "'");

        return reason;
    }

    /// <summary>Everything needed to reproduce one mutant, printed only when one fails.</summary>
    static string Reproduce(MutationClass kind, string golden, int index, byte[] mutant)
        => FormattableString.Invariant(
            $"seed 0x{Seed:X8}, class {kind}, golden {golden}, mutant {index}, bytes {Convert.ToHexString(mutant)}");

    /// <summary>
    /// A payload nesting kind 132 <paramref name="depth"/> levels, where depth 1 is one socket carrying a
    /// plain nested payload and is LEGAL.
    /// </summary>
    static byte[] Nest(int depth)
    {
        byte[] payload = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 55)
            .ToArray();

        for (int level = 0; level < depth; level++)
        {
            payload = new ItemInstancePayloadBuilder()
                .AddSockets(new[] { new InstanceSocket(7, 833, 4201, payload) })
                .ToArray();
        }

        return payload;
    }

    /// <summary>
    /// Every golden beside the assembly, listed rather than named, so a golden a later task checks in is
    /// fuzzed without a line changing here. Sorted, because a directory listing's order is not defined and
    /// the corpus has to be the same sequence on every machine.
    /// </summary>
    static IReadOnlyList<string> Corpus()
    {
        string[] found = Directory.GetFiles(
            Path.Combine(AppContext.BaseDirectory, "Payload", "Goldens"),
            "*.bin");
        Array.Sort(found, StringComparer.Ordinal);
        return found;
    }
}

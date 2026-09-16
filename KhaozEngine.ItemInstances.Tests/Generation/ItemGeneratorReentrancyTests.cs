using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Generation;

/// <summary>
/// The one property of <see cref="ItemGenerator"/> that is about the OBJECT rather than about a roll: it is
/// not reentrant, so a craft executor holds one per thread.
/// <para>
/// The doc comment says so and this says what "not reentrant" actually costs, which is the part a reader
/// would otherwise guess. It is not a crash and not a corrupted buffer. Every working array is instance
/// state a roll writes and reads back inside one call, so a second call that starts mid-roll hands the
/// outer one the inner one's scratch, and what comes out is a legal-looking item nobody authored.
/// </para>
/// <para>
/// A THREAD would prove the same thing and prove it flakily, so the re-entry here is deterministic: the
/// random source calls back into the same generator, which is the shape a craft executor with one shared
/// generator and a source that logs through a user hook would actually produce. The nested roll is a FORCED
/// UNIQUE, which draws nothing at all (spec 9.4 step 2), so the outer roll's own draw sequence is untouched
/// and the only thing that moved is the scratch.
/// </para>
/// </summary>
public class ItemGeneratorReentrancyTests
{
    [Fact]
    public void A_re_entrant_call_hands_the_outer_roll_the_inner_rolls_affix_list()
    {
        // The unique's line is an ordinary mod row with no weight row anywhere, so no roll can ever place
        // mod 6 on a rare. Finding it on one is the scratch, and nothing else.
        GenerationContext nested = new(
            GenerationWorld.Greatsword,
            ItemLevel: 60,
            ForcedRarityId: GenerationWorld.RareRarity,
            ForcedUniqueTemplateId: GenerationWorld.SunbrandTemplate,
            Quality: 0);

        var source = new ReentrantRandomSource(new SeededRandomSource(11), atCall: 5, nested);
        ItemGenerator generator = GenerationWorld.Generator(source);
        source.Generator = generator;

        GenerationResult reentered = generator.Generate(
            new GenerationContext(GenerationWorld.Greatsword, 60, GenerationWorld.RareRarity, 0, 0));
        List<InstanceAffix> affixes = GenerationWorld.Affixes(reentered.Payload);

        Assert.Contains(affixes, affix => affix.ModId == 6);
        Assert.True(source.Reentered, "The source never re-entered, so the fact read an ordinary roll.");

        // The same seed on a generator nothing re-enters rolls an item with no unique line on it at all,
        // which is what says the line above came from the nested call rather than from the pool.
        GenerationResult clean = GenerationWorld.Generator(new SeededRandomSource(11)).Generate(
            new GenerationContext(GenerationWorld.Greatsword, 60, GenerationWorld.RareRarity, 0, 0));
        Assert.DoesNotContain(GenerationWorld.Affixes(clean.Payload), affix => affix.ModId == 6);

        // And a SECOND generator over the same tables is the supported shape: the nested roll runs on its
        // own object, the outer roll keeps its own scratch, and the item is the clean one byte for byte.
        var shared = new ReentrantRandomSource(new SeededRandomSource(11), atCall: 5, nested);
        shared.Generator = GenerationWorld.Generator(new SeededRandomSource(99));
        GenerationResult apart = GenerationWorld.Generator(shared).Generate(
            new GenerationContext(GenerationWorld.Greatsword, 60, GenerationWorld.RareRarity, 0, 0));

        Assert.True(shared.Reentered);
        Assert.True(ItemInstancePayload.SequenceEqual(clean.Payload.Span, apart.Payload.Span));
    }
}

/// <summary>
/// A source that calls back into a generator on a chosen call, which is a re-entry a single thread can make
/// and a fact can pin. Point it at the generator it is DRIVING and the re-entry shares that generator's
/// scratch. Point it at another and it does not, which is the supported shape.
/// </summary>
internal sealed class ReentrantRandomSource(IRandomSource inner, int atCall, GenerationContext nested)
    : IRandomSource
{
    int _calls;

    /// <summary>The generator the re-entry lands on, set after construction because it needs this source.</summary>
    public ItemGenerator? Generator { get; set; }

    /// <summary>Whether the re-entry actually happened, so a fact cannot pass by never taking it.</summary>
    public bool Reentered { get; private set; }

    /// <inheritdoc />
    public int NextInt(int minInclusive, int maxExclusive)
    {
        Reenter();
        return inner.NextInt(minInclusive, maxExclusive);
    }

    /// <inheritdoc />
    public ulong NextULong() => inner.NextULong();

    /// <inheritdoc />
    public ushort NextRollPosition() => inner.NextRollPosition();

    /// <inheritdoc />
    public void NextBytes(Span<byte> destination) => inner.NextBytes(destination);

    /// <inheritdoc />
    public void Skip()
    {
        Reenter();
        inner.Skip();
    }

    void Reenter()
    {
        if (++_calls != atCall || Generator is null)
        {
            return;
        }

        // A forced unique draws NOTHING, so the outer roll's draw sequence is untouched by the re-entry and
        // the only thing that can have moved is the scratch.
        Reentered = true;
        _ = Generator.Generate(nested);
    }
}

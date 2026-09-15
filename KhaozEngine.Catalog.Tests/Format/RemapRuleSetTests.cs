using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Format;

/// <summary>
/// The ordered rule set of contracts 8.3: the idempotence check the publish validator owes, and the one
/// pass that answers the id a page should now carry.
/// <para>
/// Idempotence is a property of the SET rather than of one rule, and the pass that has to be idempotent is
/// the one a crash interrupts: the page bytes were rewritten and the stamp was not committed, so the retry
/// runs the same rules over the already-rewritten ids. Every double-apply test here therefore repeats at
/// the OLD stamp rather than at the new one.
/// </para>
/// </summary>
public class RemapRuleSetTests
{
    static readonly ContentTypeId Item = new(1);
    static readonly ContentTypeId Tag = new(2);

    static RemapRule Rule(int sequence, int introducedIn, ContentTypeId type, RemapRuleKind kind, int fromId, int toId, params byte[] payload)
        => new(sequence, introducedIn, type, kind, fromId, toId, payload);

    static byte[] Cap(int newCap)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, newCap);
        return payload;
    }

    static int Once(RemapRuleSet set, ContentTypeId type, int id, int pageStamp)
        => set.TryResolve(type, id, pageStamp, out int toId, out _, out _) ? toId : id;

    static int Twice(RemapRuleSet set, ContentTypeId type, int id, int pageStamp)
        => Once(set, type, Once(set, type, id, pageStamp), pageStamp);

    [Fact]
    public void ASetHoldsItsRulesInSequenceOrderAndRefusesAnythingElse()
    {
        var set = new RemapRuleSet([Rule(1, 4, Item, RemapRuleKind.ReplacedBy, 5, 9), Rule(2, 7, Item, RemapRuleKind.ReplacedBy, 6, 10)]);

        Assert.Equal(2, set.Rules.Count);
        Assert.Equal(1, set.Rules[0].Sequence);

        Assert.Throws<ArgumentException>(() => new RemapRuleSet(
            [Rule(2, 4, Item, RemapRuleKind.ReplacedBy, 5, 9), Rule(1, 7, Item, RemapRuleKind.ReplacedBy, 6, 10)]));
        Assert.Throws<ArgumentException>(() => new RemapRuleSet(
            [Rule(1, 4, Item, RemapRuleKind.ReplacedBy, 5, 9), Rule(1, 7, Item, RemapRuleKind.ReplacedBy, 6, 10)]));
    }

    [Fact]
    public void TheActiveStampIsTheNewestVersionTheSetBringsAPageTo()
    {
        var set = new RemapRuleSet([Rule(1, 4, Item, RemapRuleKind.ReplacedBy, 5, 9), Rule(2, 18, Item, RemapRuleKind.ReplacedBy, 6, 10)]);

        Assert.Equal(18, set.ActiveStamp);
        Assert.Equal(0, new RemapRuleSet([]).ActiveStamp);
    }

    [Fact]
    public void ARuleIsANoOpOnAnIdItDoesNotName()
    {
        var set = new RemapRuleSet([Rule(1, 4, Item, RemapRuleKind.ReplacedBy, 5, 9)]);

        Assert.False(set.TryResolve(Item, 77, 0, out int toId, out _, out _));
        Assert.Equal(77, toId);
    }

    [Fact]
    public void ARuleOnAnotherTypeNeverApplies()
    {
        var set = new RemapRuleSet([Rule(1, 4, Item, RemapRuleKind.ReplacedBy, 5, 9)]);

        Assert.False(set.TryResolve(Tag, 5, 0, out int toId, out _, out _));
        Assert.Equal(5, toId);
        Assert.True(set.TryResolve(Item, 5, 0, out int applied, out _, out _));
        Assert.Equal(9, applied);
    }

    [Fact]
    public void ARuleIsAppliedOnlyToAPageStrictlyOlderThanIt()
    {
        var set = new RemapRuleSet([Rule(1, 5, Item, RemapRuleKind.ReplacedBy, 5, 9)]);

        Assert.True(set.TryResolve(Item, 5, 4, out int older, out _, out _));
        Assert.Equal(9, older);

        // A page already at the rule's own version has been through it, so applying it again would chain.
        Assert.False(set.TryResolve(Item, 5, 5, out int same, out _, out _));
        Assert.Equal(5, same);
        Assert.False(set.TryResolve(Item, 5, 9, out _, out _, out _));
    }

    [Fact]
    public void AForwardChainResolvesInOnePass()
    {
        // Contracts 8.3: all of them, in Sequence order, in ONE pass. The second rule sees what the first
        // produced, which is what makes the pass a scan rather than a fixed point.
        var set = new RemapRuleSet(
        [
            Rule(1, 4, Item, RemapRuleKind.ReplacedBy, 5, 9),
            Rule(2, 4, Item, RemapRuleKind.ReplacedBy, 9, 12),
        ]);

        Assert.True(set.IsIdempotent(out _));
        Assert.Equal(12, Once(set, Item, 5, 0));
        Assert.Equal(12, Twice(set, Item, 5, 0));
    }

    [Fact]
    public void AChainWhoseDestinationIsAnEarlierRulesSourceIsNotIdempotent()
    {
        // The forbidden shape of contracts 8.3, and the one the publish validator MUST reject: rule 2 lands
        // an id on rule 1's source, so a second pass over the same stamp moves it again.
        RemapRule offender = Rule(2, 4, Item, RemapRuleKind.ReplacedBy, 5, 9);
        var set = new RemapRuleSet([Rule(1, 4, Item, RemapRuleKind.ReplacedBy, 9, 12), offender]);

        Assert.False(set.IsIdempotent(out RemapRule? flagged));
        Assert.NotNull(flagged);
        Assert.Equal(offender.Sequence, flagged.Sequence);

        // The guard is guarding a REAL failure, not a hypothetical one.
        Assert.Equal(9, Once(set, Item, 5, 0));
        Assert.Equal(12, Twice(set, Item, 5, 0));
        Assert.NotEqual(Once(set, Item, 5, 0), Twice(set, Item, 5, 0));
    }

    [Fact]
    public void ARuleThatReadsTheValueItWritesIsNotIdempotent()
    {
        // The second forbidden shape: a rule whose effect depends on a value it also changes. At the id
        // level that is a rule naming itself as its own destination, which is the degenerate case of the
        // same walk and is caught by it.
        var set = new RemapRuleSet([Rule(1, 4, Item, RemapRuleKind.ReplacedBy, 5, 5)]);

        Assert.False(set.IsIdempotent(out RemapRule? flagged));
        Assert.NotNull(flagged);
        Assert.Equal(1, flagged.Sequence);
    }

    [Fact]
    public void ADestinationOnAnotherTypeIsNotAChain()
    {
        // The check is per type, because two types' id spaces are unrelated.
        var set = new RemapRuleSet(
        [
            Rule(1, 4, Item, RemapRuleKind.ReplacedBy, 9, 12),
            Rule(2, 4, Tag, RemapRuleKind.ReplacedBy, 5, 9),
        ]);

        Assert.True(set.IsIdempotent(out _));
    }

    [Fact]
    public void AWellFormedSetIsIdempotentOverAGeneratedCorpus()
    {
        var rules = new List<RemapRule>();
        for (int i = 1; i <= 200; i++)
        {
            ContentTypeId type = i % 2 == 0 ? Item : Tag;
            rules.Add((i % 4) switch
            {
                0 => Rule(i, 4 + (i / 10), type, RemapRuleKind.ReplacedBy, i, 100_000 + i),
                1 => Rule(i, 4 + (i / 10), type, RemapRuleKind.Retired, i, 0, RemapRule.RetirePolicyPlaceholder),
                2 => Rule(i, 4 + (i / 10), type, RemapRuleKind.MovedToLegacy, i, 200_000 + i),
                _ => Rule(i, 4 + (i / 10), type, RemapRuleKind.StackCapLowered, i, 0, Cap(16)),
            });
        }

        var set = new RemapRuleSet(rules);
        Assert.True(set.IsIdempotent(out _));

        for (int id = 1; id <= 300; id++)
        {
            Assert.Equal(Once(set, Item, id, 0), Twice(set, Item, id, 0));
            Assert.Equal(Once(set, Tag, id, 0), Twice(set, Tag, id, 0));
            Assert.Equal(Once(set, Item, id, 12), Twice(set, Item, id, 12));
        }
    }

    [Fact]
    public void ARetiredPlaceholderKeepsTheIdAndSaysWhatHappenedToIt()
    {
        var set = new RemapRuleSet([Rule(1, 4, Item, RemapRuleKind.Retired, 31, 0, RemapRule.RetirePolicyPlaceholder)]);

        Assert.True(set.TryResolve(Item, 31, 0, out int toId, out RemapRuleKind kind, out ReadOnlySpan<byte> payload));
        Assert.Equal(31, toId);
        Assert.Equal(RemapRuleKind.Retired, kind);
        Assert.Equal(RemapRule.RetirePolicyPlaceholder, payload[0]);
        Assert.Equal(31, Twice(set, Item, 31, 0));
    }

    [Fact]
    public void ARetiredReplacementBehavesLikeReplacedBy()
    {
        byte[] payload = new byte[5];
        payload[0] = RemapRule.RetirePolicyReplacement;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), 4242);
        var set = new RemapRuleSet([Rule(1, 4, Item, RemapRuleKind.Retired, 31, 0, payload)]);

        Assert.True(set.TryResolve(Item, 31, 0, out int toId, out RemapRuleKind kind, out _));
        Assert.Equal(4242, toId);
        Assert.Equal(RemapRuleKind.Retired, kind);
        Assert.Equal(4242, Twice(set, Item, 31, 0));
    }

    [Fact]
    public void AStackCapLoweredKeepsTheIdAndCarriesTheNewCap()
    {
        var set = new RemapRuleSet([Rule(1, 4, Item, RemapRuleKind.StackCapLowered, 77, 0, Cap(16))]);

        Assert.True(set.TryResolve(Item, 77, 0, out int toId, out RemapRuleKind kind, out ReadOnlySpan<byte> payload));
        Assert.Equal(77, toId);
        Assert.Equal(RemapRuleKind.StackCapLowered, kind);
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(payload));
    }

    [Fact]
    public void AnEmptySetResolvesNothing()
    {
        var set = new RemapRuleSet([]);

        Assert.True(set.IsIdempotent(out _));
        Assert.False(set.TryResolve(Item, 5, 0, out int toId, out _, out _));
        Assert.Equal(5, toId);
    }
}

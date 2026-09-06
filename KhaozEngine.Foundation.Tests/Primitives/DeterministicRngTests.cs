using System;
using System.Linq;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class DeterministicRngTests
{
    [Fact]
    public void SameSeedSameSequence()
    {
        var a = new DeterministicRng(1337);
        var b = new DeterministicRng(1337);
        var sa = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var sb = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        Assert.Equal(sa, sb);
        Assert.True(sa.Distinct().Count() > 8);             // not a constant stream
    }

    [Fact]
    public void KnownVectorLocksAlgorithm()
    {
        // Captured from the implementation (the xorshift128+-derived recurrence seeded via splitmix64,
        // seed 42). This vector is why the recurrence stays as it is: the stream is a shipped contract.
        var r = new DeterministicRng(42);
        ulong[] expected = { 12706997879443677767, 13388708669165669496, 16395596082725179435 };
        Assert.Equal(expected, new[] { r.NextULong(), r.NextULong(), r.NextULong() });
    }

    [Fact]
    public void NextULong_IsTheDerivedVariantNotCanonicalXorshift128Plus()
    {
        // The state update is Vigna's xorshift128+ word for word, but the sum is taken over the NEW second
        // word and the old one, where the canonical generator sums both words as they stood before the
        // update. Pinning both halves keeps the class doc honest: the derived stream is what ships, and the
        // canonical stream is what it is NOT. The variant is deliberate (persisted State, seed-generated
        // content and two known vectors all ride on this stream), so it must not drift into the canonical
        // form by a well-meant tidy-up either.
        var rng = new DeterministicRng(42);
        (ulong derived0, ulong derived1) = rng.State;
        (ulong canonical0, ulong canonical1) = rng.State;
        for (int i = 0; i < 8; i++)
        {
            ulong drawn = rng.NextULong();
            Assert.Equal(DerivedVariant(ref derived0, ref derived1), drawn);
            Assert.NotEqual(CanonicalXorshift128Plus(ref canonical0, ref canonical1), drawn);
        }
    }

    /// <summary>Vigna's reference next(): the sum of both state words BEFORE either is mutated.</summary>
    static ulong CanonicalXorshift128Plus(ref ulong state0, ref ulong state1)
    {
        ulong s1 = state0, s0 = state1;
        ulong result = s0 + s1;
        state0 = s0;
        s1 ^= s1 << 23;
        state1 = s1 ^ s0 ^ (s1 >> 18) ^ (s0 >> 5);
        return result;
    }

    /// <summary>What DeterministicRng actually emits: the same update, summed AFTER it.</summary>
    static ulong DerivedVariant(ref ulong state0, ref ulong state1)
    {
        ulong s1 = state0, s0 = state1;
        state0 = s0;
        s1 ^= s1 << 23;
        state1 = s1 ^ s0 ^ (s1 >> 18) ^ (s0 >> 5);
        return state1 + s0;
    }

    [Fact]
    public void StateRoundTrips()
    {
        var r = new DeterministicRng(99);
        r.NextULong(); r.NextULong();
        var saved = r.State;
        ulong next = r.NextULong();
        var restored = new DeterministicRng(1) { State = saved };
        Assert.Equal(next, restored.NextULong());          // resumes the exact sequence
    }

    [Fact]
    public void RangeAndFloatBounds()
    {
        var r = new DeterministicRng(7);
        for (int i = 0; i < 1000; i++)
        {
            int n = r.Next(10, 20);
            Assert.InRange(n, 10, 19);
            float f = r.NextFloat();
            Assert.InRange(f, 0f, 0.99999994f);
        }
    }

    [Fact]
    public void CreateDerived_SameNameSameStream()
    {
        var a = new DeterministicRng(2024).CreateDerived("combat");
        var b = new DeterministicRng(2024).CreateDerived("combat");
        var sa = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var sb = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        Assert.Equal(sa, sb);
    }

    [Fact]
    public void CreateDerived_DifferentNamesDifferentStreams()
    {
        var parent = new DeterministicRng(2024);
        var combat = parent.CreateDerived("combat");
        var ore = parent.CreateDerived("oreField");
        var sc = Enumerable.Range(0, 16).Select(_ => combat.NextULong()).ToArray();
        var so = Enumerable.Range(0, 16).Select(_ => ore.NextULong()).ToArray();
        Assert.NotEqual(sc, so);
    }

    [Fact]
    public void CreateDerived_DifferentParentSeedsDifferentStreams()
    {
        var a = new DeterministicRng(1).CreateDerived("combat");
        var b = new DeterministicRng(2).CreateDerived("combat");
        var sa = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        var sb = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        Assert.NotEqual(sa, sb);
    }

    [Fact]
    public void CreateDerived_StreamsAreIndependent()
    {
        // Drain one named stream from one parent; an untouched parent's same-named
        // stream must be unaffected, and a sibling name must match across both parents.
        var p1 = new DeterministicRng(42);
        var combat1 = p1.CreateDerived("combat");
        var ore1 = p1.CreateDerived("oreField");
        for (int i = 0; i < 50; i++) combat1.NextULong();

        var p2 = new DeterministicRng(42);
        var ore2 = p2.CreateDerived("oreField");

        var s1 = Enumerable.Range(0, 32).Select(_ => ore1.NextULong()).ToArray();
        var s2 = Enumerable.Range(0, 32).Select(_ => ore2.NextULong()).ToArray();
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void CreateDerived_IsOrderIndependent()
    {
        // Derivation comes from the construction seed, not live draw state:
        // draining the parent first must not change the derived stream.
        var early = new DeterministicRng(7).CreateDerived("combat");

        var parent = new DeterministicRng(7);
        for (int i = 0; i < 100; i++) parent.NextULong();
        var late = parent.CreateDerived("combat");

        var se = Enumerable.Range(0, 16).Select(_ => early.NextULong()).ToArray();
        var sl = Enumerable.Range(0, 16).Select(_ => late.NextULong()).ToArray();
        Assert.Equal(se, sl);
    }

    [Fact]
    public void CreateDerived_KnownVectorLocksDerivation()
    {
        // Captured from the implementation: DeterministicRng(42).CreateDerived("combat").
        // Locks hash (64-bit DJB2-xor) + combine (seed ^ hash) + splitmix64 + the derived recurrence.
        var r = new DeterministicRng(42).CreateDerived("combat");
        ulong[] expected = { 9806816559159912542, 11064271574511955243, 16628530826375170203 };
        Assert.Equal(expected, new[] { r.NextULong(), r.NextULong(), r.NextULong() });
    }

    [Fact]
    public void Next_NonPositiveMax_Throws()
    {
        var rng = new DeterministicRng(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(-5));
    }

    [Fact]
    public void NextRange_MaxNotAboveMin_Throws()
    {
        var rng = new DeterministicRng(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.Next(5, 3));
    }

    [Fact]
    public void Next_ValidRanges_StillWork()
    {
        var rng = new DeterministicRng(1);
        int a = rng.Next(10);
        Assert.InRange(a, 0, 9);
        int b = rng.Next(-3, 4);
        Assert.InRange(b, -3, 3);
    }

    [Fact]
    public void CreateDerived_NullNameThrows()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => new DeterministicRng(1).CreateDerived(null!));
    }

    [Fact]
    public void CreateDerived_DoesNotPerturbParentStream()
    {
        // Deriving a child must not advance or alter the parent's own stream.
        var control = new DeterministicRng(2024);
        var expected = Enumerable.Range(0, 16).Select(_ => control.NextULong()).ToArray();

        var parent = new DeterministicRng(2024);
        parent.CreateDerived("combat");
        parent.CreateDerived("oreField");
        var actual = Enumerable.Range(0, 16).Select(_ => parent.NextULong()).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StableHash_IsPublicAndDeterministic()
    {
        Assert.Equal(DeterministicRng.StableHash("combat"), DeterministicRng.StableHash("combat"));
        Assert.NotEqual(DeterministicRng.StableHash("combat"), DeterministicRng.StableHash("oreField"));
    }

    [Fact]
    public void CreateDerived_IsIndependentOfDrawState()
    {
        var parent = new DeterministicRng(42);
        var d1 = parent.CreateDerived("combat");
        parent.NextULong(); parent.NextULong();
        var d2 = parent.CreateDerived("combat");
        Assert.Equal(d1.NextULong(), d2.NextULong());
    }
}

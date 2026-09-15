using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

/// <summary>
/// The <see cref="IRandomSource"/> seam of content contracts 14.1, 14.2 and 14.4. The seam is narrow on
/// purpose: no seed, no state and no derived stream, because a source whose seed is readable is a source a
/// crafting system can leak. Nothing here returns a float, which is contracts 13.4 applied to the roll path.
/// </summary>
public class RandomSourceTests
{
    [Fact]
    public void NextIntRejectsAnEmptyRange()
    {
        IRandomSource seeded = new SeededRandomSource(7);
        IRandomSource crypto = new CryptographicRandomSource();

        Assert.Throws<ArgumentOutOfRangeException>(() => seeded.NextInt(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => seeded.NextInt(5, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => crypto.NextInt(5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => crypto.NextInt(int.MaxValue, int.MinValue));
    }

    [Fact]
    public void NextIntOverAOneWideRangeReturnsTheValueWithoutDrawing()
    {
        // A one-wide range has one answer, so it must not consume a draw: the stream after it has to be the
        // stream a source that never saw the call would produce. That is what makes a degenerate weight table
        // safe to roll against without shifting every later draw.
        var withCall = new SeededRandomSource(99);
        var without = new SeededRandomSource(99);

        Assert.Equal(42, withCall.NextInt(42, 43));
        Assert.Equal(int.MinValue, withCall.NextInt(int.MinValue, int.MinValue + 1));

        ulong[] after = Enumerable.Range(0, 8).Select(_ => withCall.NextULong()).ToArray();
        ulong[] untouched = Enumerable.Range(0, 8).Select(_ => without.NextULong()).ToArray();
        Assert.Equal(untouched, after);
    }

    [Fact]
    public void NextIntStaysInRangeIncludingTheFullIntRange()
    {
        IRandomSource source = new SeededRandomSource(2026);
        for (int i = 0; i < 4096; i++)
        {
            int narrow = source.NextInt(-3, 4);
            Assert.InRange(narrow, -3, 3);

            int full = source.NextInt(int.MinValue, int.MaxValue);
            Assert.InRange(full, int.MinValue, int.MaxValue - 1);
        }
    }

    [Fact]
    public void SameSeedSameSequence()
    {
        var a = new SeededRandomSource(1337);
        var b = new SeededRandomSource(1337);

        ulong[] longsA = Enumerable.Range(0, 32).Select(_ => a.NextULong()).ToArray();
        ulong[] longsB = Enumerable.Range(0, 32).Select(_ => b.NextULong()).ToArray();
        Assert.Equal(longsB, longsA);

        int[] intsA = Enumerable.Range(0, 32).Select(_ => a.NextInt(0, 1000)).ToArray();
        int[] intsB = Enumerable.Range(0, 32).Select(_ => b.NextInt(0, 1000)).ToArray();
        Assert.Equal(intsB, intsA);
        Assert.True(longsA.Distinct().Count() > 16);   // not a constant stream
    }

    [Fact]
    public void DifferentSeedsDiverge()
    {
        var a = new SeededRandomSource(1);
        var b = new SeededRandomSource(2);

        ulong[] longsA = Enumerable.Range(0, 32).Select(_ => a.NextULong()).ToArray();
        ulong[] longsB = Enumerable.Range(0, 32).Select(_ => b.NextULong()).ToArray();
        Assert.NotEqual(longsB, longsA);
    }

    [Fact]
    public void NextRollPositionCoversTheWholeRange()
    {
        IRandomSource source = new SeededRandomSource(4242);
        bool sawZero = false;
        bool sawMax = false;
        for (int i = 0; i < 1_000_000; i++)
        {
            ushort roll = source.NextRollPosition();
            Assert.InRange(roll, (ushort)0, ushort.MaxValue);
            if (roll == 0) sawZero = true;
            if (roll == ushort.MaxValue) sawMax = true;
        }

        Assert.True(sawZero, "a roll position of 0 never came up in a million draws");
        Assert.True(sawMax, "a roll position of 65535 never came up in a million draws");
    }

    [Fact]
    public void NextBytesFillsTheWholeDestination()
    {
        // Every index has to be written, so the test watches each one change across several seeds rather than
        // asserting on a single draw, where a legitimate 0xAA would look like an untouched byte.
        const byte Sentinel = 0xAA;
        var touched = new bool[64];
        for (ulong seed = 0; seed < 16; seed++)
        {
            IRandomSource source = new SeededRandomSource(seed);
            var buffer = new byte[64];
            Array.Fill(buffer, Sentinel);
            source.NextBytes(buffer);
            for (int i = 0; i < buffer.Length; i++)
                if (buffer[i] != Sentinel) touched[i] = true;
        }

        Assert.DoesNotContain(false, touched);
    }

    [Fact]
    public void NextBytesAcceptsAnEmptyDestination()
    {
        IRandomSource seeded = new SeededRandomSource(5);
        IRandomSource crypto = new CryptographicRandomSource();
        seeded.NextBytes(Span<byte>.Empty);
        crypto.NextBytes(Span<byte>.Empty);
    }

    [Fact]
    public void CryptographicSourceHasNoModuloBiasOverANarrowRange()
    {
        // A bias smoke test rather than a statistics suite: 10,000 draws into 3 buckets, chi-square with two
        // degrees of freedom against a deliberately loose bound. Modulo over a range that does not divide the
        // draw width is what this catches, which is a real edge a player can farm on a crafting roll.
        const int Draws = 10_000;
        const int Buckets = 3;
        IRandomSource source = new CryptographicRandomSource();
        var counts = new int[Buckets];
        for (int i = 0; i < Draws; i++)
        {
            int bucket = source.NextInt(0, Buckets);
            Assert.InRange(bucket, 0, Buckets - 1);
            counts[bucket]++;
        }

        // chi2 = sum((observed - N/3)^2 / (N/3)), rearranged to integers as
        // sum((3 * observed - N)^2) / (3N), so the whole statistic stays off the floating point path.
        long numerator = counts.Sum(c => (long)(Buckets * c - Draws) * (Buckets * c - Draws));
        const long Bound = 30;   // chi2 of 30 at two degrees of freedom is far past any honest source
        Assert.True(
            numerator < Bound * Buckets * Draws,
            $"chi-square numerator {numerator} over buckets [{string.Join(", ", counts)}] exceeds the bound");
    }

    [Fact]
    public void CryptographicSourceDrawsDifferentSequences()
    {
        IRandomSource a = new CryptographicRandomSource();
        IRandomSource b = new CryptographicRandomSource();
        ulong[] longsA = Enumerable.Range(0, 16).Select(_ => a.NextULong()).ToArray();
        ulong[] longsB = Enumerable.Range(0, 16).Select(_ => b.NextULong()).ToArray();
        Assert.NotEqual(longsB, longsA);
    }

    [Theory]
    [InlineData(typeof(SeededRandomSource))]
    [InlineData(typeof(CryptographicRandomSource))]
    public void NeitherSourceExposesItsSeedItsStateOrADerivedStream(Type sourceType)
    {
        string[] forbidden = ["Seed", "State", "CreateDerived"];
        string[] members = sourceType
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToArray();

        foreach (string name in forbidden)
            Assert.DoesNotContain(name, members);
    }

    [Fact]
    public void TheSeamExposesFourMembersAndNoFloat()
    {
        MethodInfo[] methods = typeof(IRandomSource).GetMethods();
        Assert.Equal(4, methods.Length);
        Assert.All(methods, m => Assert.NotEqual(typeof(float), m.ReturnType));
        Assert.All(methods, m => Assert.NotEqual(typeof(double), m.ReturnType));
    }
}

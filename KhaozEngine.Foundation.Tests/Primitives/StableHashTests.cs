using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Primitives;

public class StableHashTests
{
    // The reference consumer's algorithm (Ruinborne AbilityVfxRegistry.HashCombine), reproduced verbatim as the
    // known-answer oracle: FNV-1a fold of the offset basis + prime, then a Murmur3-style finalizer avalanche.
    // StableHash.Mix(a, b, c) must be byte-identical to this or adoption changes the procedural geometry.
    static uint GameHashCombine(uint a, uint b, uint c)
    {
        uint h = 2166136261u;
        h = (h ^ a) * 16777619u;
        h = (h ^ b) * 16777619u;
        h = (h ^ c) * 16777619u;
        h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
        return h;
    }

    static float GameUnit(uint h) => (h >> 8) * (1f / 16777216f);

    [Fact]
    public void Mix_KnownAnswers()
    {
        // Literals computed independently (uint32 arithmetic) from the FNV-1a + avalanche algorithm.
        Assert.Equal(0x261dd870u, StableHash.Mix(0u));
        Assert.Equal(0x6e48bf04u, StableHash.Mix(1u));
        Assert.Equal(0x6e82fe0cu, StableHash.Mix(0xdeadbeefu));

        Assert.Equal(0xf8841f67u, StableHash.Mix(1u, 2u));
        Assert.Equal(0xec0875aeu, StableHash.Mix(0xdeadbeefu, 0x12345678u));

        Assert.Equal(0x281ab624u, StableHash.Mix(0u, 0u, 0u));
        Assert.Equal(0x362618abu, StableHash.Mix(1u, 2u, 3u));
        Assert.Equal(0x79e48c34u, StableHash.Mix(0xdeadbeefu, 0x12345678u, 0x9abcdef0u));
    }

    [Fact]
    public void Mix3_MatchesGameAlgorithm()
    {
        // Sweep a spread of inputs and require exact equality with the consumer's own HashCombine.
        for (uint a = 0; a < 7; a++)
            for (uint b = 0; b < 7; b++)
                for (uint c = 0; c < 7; c++)
                    Assert.Equal(GameHashCombine(a, b, c), StableHash.Mix(a, b, c));

        Assert.Equal(GameHashCombine(0xffffffffu, 0x80000000u, 0x00000001u),
                     StableHash.Mix(0xffffffffu, 0x80000000u, 0x00000001u));
    }

    [Fact]
    public void Mix_IsOrderSensitive_AndDeterministic()
    {
        Assert.Equal(StableHash.Mix(1u, 2u, 3u), StableHash.Mix(1u, 2u, 3u));   // pure: same in = same out
        Assert.NotEqual(StableHash.Mix(1u, 2u, 3u), StableHash.Mix(3u, 2u, 1u)); // order matters
    }

    [Fact]
    public void ToUnitFloat_InUnitRange()
    {
        Assert.Equal(0f, StableHash.ToUnitFloat(0u));
        Assert.True(StableHash.ToUnitFloat(0xFFFFFFFFu) < 1f);        // top of the range is strictly below 1
        // Sweep many hashes (walk a Mix chain) and confirm every fold lands in [0, 1).
        uint h = 12345u;
        for (int i = 0; i < 10000; i++)
        {
            float u = StableHash.ToUnitFloat(h);
            Assert.True(u >= 0f && u < 1f, $"ToUnitFloat out of [0,1): {u}");
            h = StableHash.Mix(h, (uint)i);
        }
    }

    [Fact]
    public void ToUnitFloat_MatchesGameUnitFold()
    {
        uint h = 0x9abcdef0u;
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(GameUnit(h), StableHash.ToUnitFloat(h));
            h = StableHash.Mix(h);
        }
    }

    [Fact]
    public void ToUnitFloat_EqualsNextFloatFold_OnSameBits()
    {
        // NextFloat now delegates to ToUnitFloat, so two identically-seeded streams must agree: one drawn as a float,
        // the other drawn as a uint and folded by hand. Proves the shared fold did not change NextFloat's behaviour.
        var asFloat = new XorRng(2026);
        var asBits = new XorRng(2026);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(asFloat.NextFloat(), StableHash.ToUnitFloat(asBits.NextUInt()));
    }
}

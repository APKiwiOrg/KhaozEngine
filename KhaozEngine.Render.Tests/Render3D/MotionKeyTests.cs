using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="MotionKey"/> (TEMPORAL-FOUNDATIONS-DESIGN section 3). The pinned values are the contract with every
/// consumer that stores or derives keys: <see cref="MotionKey.Combine"/> is a fixed 64-bit mix, so the same body and
/// part give the same key in every run, process and platform. The expected values were computed from the splitmix64
/// finalizer independently of the implementation.
/// </summary>
public sealed class MotionKeyTests
{
    const ulong Golden = 0x9E3779B97F4A7C15UL;

    [Fact]
    public void None_is_the_default_and_id_zero()
    {
        Assert.True(MotionKey.None.IsNone);
        Assert.Equal(0UL, MotionKey.None.Value);
        Assert.Equal(MotionKey.None, default(MotionKey));
        Assert.Equal(MotionKey.None, MotionKey.From(0));
        Assert.False(MotionKey.From(1).IsNone);
        Assert.Equal(42UL, MotionKey.From(42).Value);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void Combine_keeps_none_none(uint part) => Assert.True(MotionKey.Combine(MotionKey.None, part).IsNone);

    [Theory]
    [InlineData(1UL, 0u, 0x910A2DEC89025CC1UL)]
    [InlineData(1UL, 1u, 0xBEEB8DA1658EEC67UL)]
    [InlineData(42UL, 7u, 0xCCF635EE9E9E2FA4UL)]
    [InlineData(ulong.MaxValue, 0u, 0xE4D971771B652C20UL)]
    [InlineData(1UL, uint.MaxValue, 0xC3FC3482A90CD79AUL)]
    public void Combine_is_pinned_across_runs_and_platforms(ulong id, uint part, ulong expected)
        => Assert.Equal(expected, MotionKey.Combine(MotionKey.From(id), part).Value);

    [Fact]
    public void Nested_combine_is_pinned_and_order_sensitive()
    {
        Assert.Equal(0x5E41AB087439611EUL, MotionKey.Combine(MotionKey.Combine(MotionKey.From(1), 0), 0).Value);
        MotionKey seven = MotionKey.From(7);
        Assert.NotEqual(MotionKey.Combine(MotionKey.Combine(seven, 1), 2),
            MotionKey.Combine(MotionKey.Combine(seven, 2), 1));
    }

    // Each id is chosen so id + (part + 1) * golden wraps to exactly zero, the one input the mixer maps to zero.
    [Theory]
    [InlineData(0x61C8864680B583EBUL, 0u)]
    [InlineData(0xC3910C8D016B07D6UL, 1u)]
    [InlineData(0x80B583EB00000000UL, uint.MaxValue)]
    public void Combine_never_yields_none_even_where_the_mixer_input_is_zero(ulong id, uint part)
    {
        MotionKey combined = MotionKey.Combine(MotionKey.From(id), part);
        Assert.False(combined.IsNone);
        Assert.Equal(Golden, combined.Value);
    }

    [Fact]
    public void Parts_of_many_bodies_never_collide_with_each_other_or_with_small_ids()
    {
        var seen = new HashSet<ulong>();
        for (ulong id = 1; id <= 512; id++)
            for (uint part = 0; part < 128; part++)
            {
                MotionKey key = MotionKey.Combine(MotionKey.From(id), part);
                Assert.False(key.IsNone);
                Assert.True(key.Value > 512, $"part {part} of body {id} fell onto the small id {key.Value}");
                Assert.True(seen.Add(key.Value), $"part {part} of body {id} collided");
            }
    }

    [Fact]
    public void One_flipped_bit_of_the_id_or_the_part_flips_about_half_the_output()
    {
        // Avalanche: a well-mixed 64-bit derivation flips 32 of 64 output bits on average for any one-bit input
        // change. A plain xor or multiply sits far from 32. Measured means are 31.99 (id) and 31.89 (part).
        long idFlips = 0, idSamples = 0, partFlips = 0, partSamples = 0;
        for (ulong id = 1; id <= 64; id++)
        {
            ulong baseline = MotionKey.Combine(MotionKey.From(id), 3).Value;
            for (int bit = 0; bit < 64; bit++)
            {
                ulong flipped = id ^ (1UL << bit);
                if (flipped == 0) continue;
                idFlips += BitOperations.PopCount(baseline ^ MotionKey.Combine(MotionKey.From(flipped), 3).Value);
                idSamples++;
            }
            ulong partBaseline = MotionKey.Combine(MotionKey.From(id), 5).Value;
            for (int bit = 0; bit < 32; bit++)
            {
                partFlips += BitOperations.PopCount(
                    partBaseline ^ MotionKey.Combine(MotionKey.From(id), 5u ^ (1u << bit)).Value);
                partSamples++;
            }
        }
        Assert.InRange(idFlips / (double)idSamples, 30.0, 34.0);
        Assert.InRange(partFlips / (double)partSamples, 30.0, 34.0);
    }

    [Fact]
    public void Equality_hash_and_text_follow_the_value()
    {
        MotionKey a = MotionKey.From(0x1234_5678_9ABC_DEF0), b = MotionKey.From(0x1234_5678_9ABC_DEF0);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, MotionKey.From(0x1234_5678_9ABC_DEF1));
        Assert.Equal("MotionKey.None", MotionKey.None.ToString());
        Assert.Equal("MotionKey(0x123456789ABCDEF0)", a.ToString());
    }
}

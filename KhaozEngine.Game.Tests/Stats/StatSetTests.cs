using System;
using KhaozEngine.Stats;
using Xunit;

namespace KhaozEngine.Tests;

public class StatSetTests
{
    // ----- bit-for-bit determinism (the important one) ------------------------

    [Fact]
    public void AddThenRemoveSource_RestoresBaseValue_BitForBit()
    {
        // A set that never saw a source at all: the reference bit pattern.
        var untouched = new StatSet(1);
        untouched.SetBase(0, 13.7f);
        int expectedBits = BitConverter.SingleToInt32Bits(untouched.Value(0));

        var s = new StatSet(1);
        s.SetBase(0, 13.7f);
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 4.3f, 0.15f) });
        s.RemoveSource(new StatSourceId(1));

        int actualBits = BitConverter.SingleToInt32Bits(s.Value(0));
        Assert.Equal(expectedBits, actualBits);
    }

    [Fact]
    public void AddSecondSourceThenRemoveIt_RestoresFirstSourceOnlyValue_BitForBit()
    {
        var s = new StatSet(1);
        s.SetBase(0, 13.7f);
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 4.3f, 0.15f) });
        int expectedBits = BitConverter.SingleToInt32Bits(s.Value(0)); // value with only source A

        s.AddSource(new StatSourceId(2), new[] { new StatModifier(0, 9.1f, -0.22f) });
        s.RemoveSource(new StatSourceId(2));

        int actualBits = BitConverter.SingleToInt32Bits(s.Value(0));
        Assert.Equal(expectedBits, actualBits);
    }

    // ----- the fold -------------------------------------------------------------

    [Fact]
    public void Fold_AppliesFlatBeforePercentMultiplier()
    {
        var s = new StatSet(1);
        s.SetBase(0, 10f);
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 5f, 1f) }); // +5 flat, +100%

        // (10 + 5) * (1 + 1) = 30, not 10 * (1 + 1) + 5 = 25.
        Assert.Equal(30f, s.Value(0));
    }

    [Fact]
    public void MultipleSourcesOnSameChannel_SumFlatsAndPercents_OneFold()
    {
        var s = new StatSet(1);
        s.SetBase(0, 100f);
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 10f, 0.25f) });
        s.AddSource(new StatSourceId(2), new[] { new StatModifier(0, 20f, 0.5f) });

        // flats: 10 + 20 = 30, percents: 0.25 + 0.5 = 0.75 -> (100 + 30) * 1.75 = 227.5.
        Assert.Equal(227.5f, s.Value(0));
    }

    [Fact]
    public void OneSourceSpanningSeveralChannels_AppliesEachModifierToItsOwnChannel()
    {
        var s = new StatSet(3);
        s.SetBase(0, 10f);
        s.SetBase(1, 20f);
        s.SetBase(2, 30f);
        s.AddSource(new StatSourceId(1), new[]
        {
            new StatModifier(0, 1f, 0f),
            new StatModifier(2, 2f, 0f),
        });

        Assert.Equal(11f, s.Value(0));
        Assert.Equal(20f, s.Value(1)); // untouched by the source: stays at base
        Assert.Equal(32f, s.Value(2));
    }

    // ----- sources: add, replace, remove, clear ----------------------------------

    [Fact]
    public void AddSource_UnderExistingId_ReplacesRatherThanAppends()
    {
        var s = new StatSet(1);
        s.SetBase(0, 100f);
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 10f, 0f) });
        Assert.Equal(110f, s.Value(0));

        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 5f, 0f) });

        Assert.Equal(105f, s.Value(0)); // the old +10 is gone, not stacked to +10 +5 = 115
        Assert.Equal(1, s.SourceCount);
    }

    [Fact]
    public void RemoveSource_NeverAdded_ReturnsFalse_AndChangesNothing()
    {
        var s = new StatSet(2);
        s.SetBase(0, 5f);
        s.SetBase(1, 7f);

        bool removed = s.RemoveSource(new StatSourceId(99));

        Assert.False(removed);
        Assert.Equal(5f, s.Value(0));
        Assert.Equal(7f, s.Value(1));
        Assert.Equal(0, s.SourceCount);
    }

    [Fact]
    public void ClearSources_ReturnsEveryChannel_ToBase()
    {
        var s = new StatSet(2);
        s.SetBase(0, 5f);
        s.SetBase(1, 13.7f);
        s.AddSource(new StatSourceId(1), new[]
        {
            new StatModifier(0, 10f, 0.5f),
            new StatModifier(1, 20f, 0.5f),
        });
        Assert.NotEqual(5f, s.Value(0)); // sanity: the source is actually affecting it

        s.ClearSources();

        Assert.Equal(5f, s.Value(0));
        Assert.Equal(13.7f, s.Value(1));
        Assert.Equal(0, s.SourceCount);
    }

    [Fact]
    public void SourceCount_TracksAddReplaceRemoveClear()
    {
        var s = new StatSet(1);
        Assert.Equal(0, s.SourceCount);

        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 1f, 0f) });
        Assert.Equal(1, s.SourceCount);

        s.AddSource(new StatSourceId(2), new[] { new StatModifier(0, 1f, 0f) });
        Assert.Equal(2, s.SourceCount);

        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 2f, 0f) }); // replace: count unchanged
        Assert.Equal(2, s.SourceCount);

        s.RemoveSource(new StatSourceId(1));
        Assert.Equal(1, s.SourceCount);

        s.ClearSources();
        Assert.Equal(0, s.SourceCount);
    }

    // ----- reading: base, empty set, bounds --------------------------------------

    [Fact]
    public void EmptySet_ReadsAsBase_OnEveryChannel()
    {
        var s = new StatSet(4);
        s.SetBase(0, 1f);
        s.SetBase(1, 2f);
        s.SetBase(2, 3f);
        s.SetBase(3, 4f);

        for (int c = 0; c < 4; c++)
            Assert.Equal(s.GetBase(c), s.Value(c));
    }

    [Fact]
    public void SourcesOnOtherChannels_LeaveUntouchedChannelExactlyAtBase()
    {
        var s = new StatSet(2);
        s.SetBase(0, 50f);
        s.SetBase(1, 13.7f); // channel 1 is never targeted by any modifier
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 100f, 2f) });

        Assert.Equal(13.7f, s.Value(1));
    }

    [Fact]
    public void SetBase_OutOfRangeChannel_Throws()
    {
        var s = new StatSet(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.SetBase(-1, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.SetBase(3, 1f));
    }

    [Fact]
    public void GetBase_OutOfRangeChannel_Throws()
    {
        var s = new StatSet(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.GetBase(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.GetBase(3));
    }

    [Fact]
    public void Value_OutOfRangeChannel_Throws()
    {
        var s = new StatSet(3);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Value(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Value(3));
    }

    [Fact]
    public void AddSource_WithOutOfRangeChannelInSpan_Throws_AndLeavesSetUntouched()
    {
        var s = new StatSet(2);
        s.SetBase(0, 1f);
        s.SetBase(1, 2f);
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 100f, 0f) });

        float before0 = s.Value(0);
        float before1 = s.Value(1);
        int beforeCount = s.SourceCount;

        var badModifiers = new[]
        {
            new StatModifier(0, 5f, 0f),
            new StatModifier(5, 1f, 0f), // channel 5 is out of range for a 2-channel set
        };

        // Same id as the existing source: validation must fail before the replace touches anything.
        Assert.Throws<ArgumentOutOfRangeException>(() => s.AddSource(new StatSourceId(1), badModifiers));

        Assert.Equal(before0, s.Value(0));
        Assert.Equal(before1, s.Value(1));
        Assert.Equal(beforeCount, s.SourceCount);
    }

    // ----- minimum scale ----------------------------------------------------------

    [Fact]
    public void MinimumScale_DefaultZero_FloorsNegativePercentStack_AtZero_NotInverted()
    {
        var s = new StatSet(1); // default minimumScale = 0
        s.SetBase(0, 100f);
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 0f, -1.5f) }); // -150%

        Assert.Equal(0f, s.Value(0)); // multiplier floored at 0, not -0.5 (would invert the sign)
    }

    [Fact]
    public void MinimumScale_NegativeInfinity_DisablesFloor_AllowsNegativeValue()
    {
        var s = new StatSet(1, float.NegativeInfinity);
        s.SetBase(0, 100f);
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 0f, -1.5f) }); // -150%

        Assert.Equal(-50f, s.Value(0)); // 100 * (1 - 1.5) = 100 * -0.5 = -50
    }

    [Fact]
    public void MinimumScale_PositiveFloor_ClampsAboveZero()
    {
        var s = new StatSet(1, 0.1f); // floors the multiplier at 10%
        s.SetBase(0, 100f);
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(0, 0f, -1.5f) }); // -150%: would floor at 0 by default

        Assert.Equal(10f, s.Value(0)); // multiplier floored at 0.1, so 100 * 0.1 = 10
    }

    // ----- bulk read (CopyValuesTo) ------------------------------------------------

    [Fact]
    public void CopyValuesTo_MatchesPerChannelValueReads()
    {
        var s = new StatSet(3);
        s.SetBase(0, 1f);
        s.SetBase(1, 2f);
        s.SetBase(2, 3f);
        s.AddSource(new StatSourceId(1), new[] { new StatModifier(1, 10f, 0.5f) });

        float[] dest = new float[3];
        s.CopyValuesTo(dest);

        Assert.Equal(s.Value(0), dest[0]);
        Assert.Equal(s.Value(1), dest[1]);
        Assert.Equal(s.Value(2), dest[2]);
    }

    [Fact]
    public void CopyValuesTo_ShortDestination_Throws()
    {
        var s = new StatSet(3);
        Assert.Throws<ArgumentException>(() => s.CopyValuesTo(new float[2]));
    }

    // ----- construction -------------------------------------------------------------

    [Fact]
    public void Constructor_NegativeChannelCount_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new StatSet(-1));

    [Fact]
    public void Constructor_ExposesChannelCountAndDefaultMinimumScale()
    {
        var s = new StatSet(5);
        Assert.Equal(5, s.ChannelCount);
        Assert.Equal(0f, s.MinimumScale);
    }
}

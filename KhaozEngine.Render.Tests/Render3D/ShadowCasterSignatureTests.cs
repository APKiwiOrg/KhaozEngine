using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class ShadowCasterSignatureTests
{
    static List<Scene3D.ShadowCasterInstance> TwoCasters() => new()
    {
        new(Matrix4x4.Identity, 0f),
        new(Matrix4x4.CreateTranslation(2f, 0f, 0f), 0f),
    };

    [Fact]
    public void PackedOffsetsDoNotChangeTheCasterSignature()
    {
        var before = new List<Scene3D.ShadowCasterSpan> { new(3, 1, 1, 2, ShadowCastKind.Opaque) };
        var after = new List<Scene3D.ShadowCasterSpan> { new(3, 1, 0, 2, ShadowCastKind.Opaque) };
        Assert.False(Scene3D.ShadowCastersChanged(before, TwoCasters(), after, TwoCasters()));
        Assert.Equal(1u, before[0].Start);
        Assert.Equal(0u, after[0].Start);
    }

    [Fact]
    public void RemovingANonCasterGapDoesNotChangeTheCasterSignature()
    {
        var before = Split();
        var after = Merged();
        Assert.False(Scene3D.ShadowCastersChanged(before, TwoCasters(), after, TwoCasters()));
        Assert.False(Scene3D.ShadowCastersChanged(after, TwoCasters(), before, TwoCasters()));
        Assert.Equal(2, before.Count);
        Assert.Equal(2u, before[1].Start);
        Assert.Equal(1u, before[1].Count);
        Assert.Equal(2u, Assert.Single(after).Count);
    }

    [Theory]
    [InlineData(4, 1, 1)]
    [InlineData(3, 2, 1)]
    [InlineData(3, 1, 2)]
    [InlineData(3, 1, 3)]
    public void AChangedMeshGenerationOrCastKindStillInvalidates(int index, int generation, int kind)
    {
        var changed = Split();
        changed[1] = new Scene3D.ShadowCasterSpan(index, generation, 2, 1, (ShadowCastKind)kind);
        Assert.True(Scene3D.ShadowCastersChanged(Merged(), TwoCasters(), changed, TwoCasters()));
    }

    [Fact]
    public void CanonicalSignatureComparisonAllocatesNothing()
    {
        var split = Split();
        var merged = Merged();
        var models = TwoCasters();
        for (int i = 0; i < 20; i++) Scene3D.ShadowCastersChanged(split, models, merged, models);
        AllocAssert.NoPerCallAllocation("canonical shadow caster signature comparison", () =>
        {
            for (int i = 0; i < 100; i++) Scene3D.ShadowCastersChanged(split, models, merged, models);
        });
        Assert.False(Scene3D.ShadowCastersChanged(split, models, merged, models));
    }

    static List<Scene3D.ShadowCasterSpan> Split() => new()
    {
        new(3, 1, 0, 1, ShadowCastKind.Opaque),
        new(3, 1, 2, 1, ShadowCastKind.Opaque),
    };

    static List<Scene3D.ShadowCasterSpan> Merged() => new()
    {
        new(3, 1, 0, 2, ShadowCastKind.Opaque),
    };
}

using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliagePatchLayoutTests
{
    static readonly MeshBounds GrassBounds = new(new Vector3(-.5f, 0f, -.5f), new Vector3(.5f, 2f, .5f));
    static FoliageInstance At(float x, float z = 0f, float rank = .1f, int mesh = 4, int generation = 1) =>
        new(new MeshHandle(mesh, generation), Matrix4x4.CreateTranslation(x, 3f, z), rank);

    [Fact]
    public void SignedCellsSeparateNegativeAndEightMetreBoundaries()
    {
        FoliageInstance[] input = [At(0f), At(7.9f), At(8f), At(-.1f), At(-8f), At(-8.1f), At(0f, -1f)];
        FoliagePatchLayout layout = FoliagePatchLayout.Build(input, _ => GrassBounds);

        Assert.Equal(5, layout.Patches.Length);
        Assert.Equal(2, Assert.Single(layout.Patches, p => p.RootMin == new Vector2(0f, 0f)).Count);
        Assert.Equal(2, Assert.Single(layout.Patches, p => p.RootMin == new Vector2(-8f, 0f)).Count);
        Assert.Single(layout.Patches, p => p.RootMin == new Vector2(8f, 0f));
        Assert.Single(layout.Patches, p => p.RootMin == new Vector2(-8.1f, 0f));
        Assert.Single(layout.Patches, p => p.RootMin == new Vector2(0f, -1f));
    }

    [Fact]
    public void MeshSlotsAndGenerationsStayInSeparateContiguousPatches()
    {
        FoliagePatchLayout layout = FoliagePatchLayout.Build(
            [At(0f), At(1f, mesh: 5), At(2f, generation: 2), At(3f)], _ => GrassBounds);

        Assert.Equal(3, layout.Patches.Length);
        int next = 0;
        foreach (FoliagePatch patch in layout.Patches)
        {
            Assert.Equal(next, patch.Start);
            Assert.All(layout.Instances[patch.Start..(patch.Start + patch.Count)], i => Assert.Equal(patch.Mesh, i.Mesh));
            next += patch.Count;
        }
        Assert.Equal(4, next);
    }

    [Fact]
    public void RanksSortStablyWithoutKeepingTheCallersArray()
    {
        FoliageInstance[] input = [At(1f, rank: .9f), At(2f), At(3f), At(4f, rank: .5f)];
        FoliageInstance[] original = input.ToArray();
        FoliagePatchLayout layout = FoliagePatchLayout.Build(input, _ => GrassBounds);

        Assert.Equal(original, input);
        Assert.Equal(new[] { 2f, 3f, 4f, 1f }, layout.Instances.Select(i => i.Transform.M41));
        input[1] = At(100f);
        Assert.Equal(2f, layout.Instances[0].Transform.M41);
    }

    [Fact]
    public void UnknownMeshesAreDroppedAndEmptyInputsAreSupported()
    {
        FoliagePatchLayout layout = FoliagePatchLayout.Build([At(0f), At(1f, mesh: 9)],
            mesh => mesh.Index == 4 ? GrassBounds : null);

        Assert.Single(layout.Instances);
        Assert.Single(layout.Patches);
        Assert.Empty(FoliagePatchLayout.Build([], _ => GrassBounds).Patches);
        Assert.Empty(FoliagePatchLayout.Build([At(0f)], _ => null).Instances);
    }

    [Fact]
    public void CustomPatchSizeControlsGrouping()
    {
        FoliagePatchLayout layout = FoliagePatchLayout.Build([At(0f), At(3.9f), At(4f)], _ => GrassBounds, 4f);

        Assert.Equal(2, layout.Patches.Length);
        Assert.Equal(2, Assert.Single(layout.Patches, p => p.RootMin.X == 0f).Count);
    }

    [Fact]
    public void PatchBoundsAndMaximumHeightAccumulateEveryInstance()
    {
        FoliageInstance tall = new(new MeshHandle(4), Matrix4x4.CreateScale(3f) *
            Matrix4x4.CreateTranslation(6f, 8f, 2f), .9f);
        FoliagePatch patch = Assert.Single(FoliagePatchLayout.Build([At(1f), tall], _ => GrassBounds).Patches);

        Assert.Equal(new Vector3(.5f, 3f, -.5f), patch.Bounds.Min);
        Assert.Equal(new Vector3(7.5f, 14f, 3.5f), patch.Bounds.Max);
        Assert.Equal(new Vector2(1f, 0f), patch.RootMin);
        Assert.Equal(new Vector2(6f, 2f), patch.RootMax);
        Assert.Equal(6f, patch.MaxHeight);
    }

    [Fact]
    public void BoundsContainTransformedGeometryAndItsOffsetRoot()
    {
        var bounds = new MeshBounds(new Vector3(-1f, 2f, -.5f), new Vector3(1f, 4f, .5f));
        var transform = new Matrix4x4(2, 0, 0, 0, 1, 2, 3, 0, 0, 0, 4, 0, 10, 20, 30, 1);
        FoliagePatch patch = Assert.Single(FoliagePatchLayout.Build(
            [new FoliageInstance(new MeshHandle(4), transform, .1f)], _ => bounds).Patches);

        Assert.Equal(new Vector3(10f, 24f, 34f), patch.Bounds.Min);
        Assert.Equal(new Vector3(16f, 28f, 44f), patch.Bounds.Max);
        Assert.Equal(new Vector2(10f, 30f), patch.RootMin);
        Assert.Equal(patch.RootMin, patch.RootMax);
        Assert.Equal(7.483315f, patch.MaxHeight, 5);
    }

    [Fact]
    public void TiltedBladeCullingContainsHorizontalBendAndTipDrop()
    {
        var up = Vector3.Normalize(new Vector3(.938066f, -.346458f, 0f));
        var transform = Matrix4x4.Identity;
        transform.M21 = up.X;
        transform.M22 = up.Y;
        var bounds = new MeshBounds(Vector3.Zero, Vector3.UnitY);
        FoliagePatch patch = Assert.Single(FoliagePatchLayout.Build(
            [new FoliageInstance(new MeshHandle(4), transform, .1f)], _ => bounds).Patches);
        Vector3 bentTip = up + new Vector3(.65f, -(1f - MathF.Sqrt(1f - .65f * .65f)), 0f);
        Assert.True(Vector3.Distance(bentTip, patch.Bounds.Center) <= patch.CullingRadius(true));
        Assert.Equal(patch.Bounds.Radius, patch.CullingRadius(false));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidPatchSizesAreRejectedEvenForEmptyInputs(float size) =>
        Assert.ThrowsAny<ArgumentException>(() => FoliagePatchLayout.Build([], _ => GrassBounds, size));

    [Theory]
    [InlineData(-.1f)]
    [InlineData(1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidRanksAreRejected(float rank) =>
        Assert.ThrowsAny<ArgumentException>(() => FoliagePatchLayout.Build([At(0f, rank: rank)], _ => GrassBounds));

    [Fact]
    public void NonFiniteProjectiveAndSingularTransformsAreRejected()
    {
        Matrix4x4[] transforms =
        [
            Matrix4x4.Identity with { M42 = float.NaN },
            Matrix4x4.Identity with { M33 = float.PositiveInfinity },
            Matrix4x4.Identity with { M14 = .1f },
            Matrix4x4.Identity with { M44 = 0f },
            Matrix4x4.CreateScale(1f, 0f, 1f),
            Matrix4x4.Identity with { M21 = 1f, M22 = 0f },
        ];

        foreach (Matrix4x4 transform in transforms)
            Assert.ThrowsAny<ArgumentException>(() => FoliagePatchLayout.Build(
                [new FoliageInstance(new MeshHandle(4), transform, .1f)], _ => GrassBounds));
    }

    [Fact]
    public void NullMeshResolverIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => FoliagePatchLayout.Build([], null!));
}

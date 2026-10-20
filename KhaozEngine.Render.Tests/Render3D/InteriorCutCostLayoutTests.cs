using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class InteriorCutCostLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothLayoutsAreDeterministic(bool typical)
    {
        var layout = typical ? InteriorCutCostScene.Layout.TypicalVillage : InteriorCutCostScene.Layout.DenseVillage;
        var first = InteriorCutCostScene.Create(layout);
        var second = InteriorCutCostScene.Create(layout);
        Assert.Equal(first.Trees, second.Trees);
        Assert.Equal(3600, first.Trees.Count);
        Assert.Equal(49, first.Interiors.Count);
        Assert.True(first.FieldFor(InteriorCutCostPerfGpuTests.Mode.VillageInteriors)!.Texels.Span.SequenceEqual(
            second.FieldFor(InteriorCutCostPerfGpuTests.Mode.VillageInteriors)!.Texels.Span));
        for (int i = 0; i < 49; i++)
            Assert.Equal(first.Interiors[i].Outline, second.Interiors[i].Outline);
    }

    [Fact]
    public void DenseVillageKeepsTheRecordedPlacementBytes()
    {
        var scene = InteriorCutCostScene.Create(InteriorCutCostScene.Layout.DenseVillage);
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        foreach (var tree in scene.Trees)
        {
            writer.Write(tree.Id);
            writer.Write(tree.X);
            writer.Write(tree.Y);
            writer.Write(tree.Z);
            writer.Write(tree.Scale);
            writer.Write(tree.Yaw);
        }
        Assert.Equal("384f2b90e60c0b2a4d0069c37fdbfa1af388f5b2e5738a5c2a152303ce041a46",
            Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
    }

    [Fact]
    public void TypicalVillageHasTwoPercentReachingTrees()
    {
        var scene = InteriorCutCostScene.Create(InteriorCutCostScene.Layout.TypicalVillage);
        InteriorHeightField field = scene.FieldFor(InteriorCutCostPerfGpuTests.Mode.VillageInteriors)!;
        // These are the three drawn meshes and transforms used by the timing source, with the renderer's
        // conservative sphere cull bounds. Count a tree once even when more than one part reaches inside.
        MeshBounds trunk = MeshBounds.FromVertices(MeshPrimitives.Box().Vertices);
        MeshBounds card = MeshBounds.FromVertices(MeshPrimitives.Plane(10f, 14f).Vertices);
        int reaching = scene.Trees.Count(tree =>
        {
            Matrix4x4 world = Matrix4x4.CreateScale(tree.Scale) * Matrix4x4.CreateRotationY(tree.Yaw)
                * Matrix4x4.CreateTranslation(tree.X, tree.Y, tree.Z);
            bool Reaches(MeshBounds bounds, Matrix4x4 transform)
            {
                bounds.WorldSphere(transform, out Vector3 centre, out float radius);
                return field.MayIntersect(centre - new Vector3(radius), centre + new Vector3(radius));
            }
            return Reaches(trunk, Matrix4x4.CreateScale(.8f, 12f, .8f)
                    * Matrix4x4.CreateTranslation(0, 6, 0) * world)
                || Enumerable.Range(0, 2).Any(side => Reaches(card, Matrix4x4.CreateRotationX(MathF.PI / 2f)
                    * Matrix4x4.CreateRotationY(side * MathF.PI / 2f) * Matrix4x4.CreateTranslation(0, 11, 0) * world));
        });
        Assert.Equal(72, reaching);
    }
}

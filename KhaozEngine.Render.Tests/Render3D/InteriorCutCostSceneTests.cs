using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class InteriorCutCostSceneTests
{
    [Fact]
    public void RotatingModesCoverAllFourBaselines()
    {
        var orders = Enumerable.Range(0, 4).Select(InteriorCutCostPerfGpuTests.OrderFor).ToArray();
        foreach (var order in orders)
        {
            Assert.Equal(4, order.Length);
            Assert.Equal(4, order.Distinct().Count());
        }
        foreach (var mode in Enum.GetValues<InteriorCutCostPerfGpuTests.Mode>())
            Assert.Equal(new[] { 0, 1, 2, 3 }, orders.Select(o => Array.IndexOf(o, mode)).Order().ToArray());
    }

    [Theory]
    [InlineData("metal-native", "Apple M2 Max", false)]
    [InlineData("metal-native", "NVIDIA Tesla T4", false)]
    [InlineData("vulkan-native", "llvmpipe", false)]
    [InlineData("direct3d11-native", "Microsoft Basic Render Driver", false)]
    [InlineData("vulkan-native", "NVIDIA RTX 4090", false)]
    [InlineData("vulkan-native", "Tesla T4", false)]
    [InlineData("vulkan-native", "NVIDIA T400", false)]
    [InlineData("direct3d11-native", "NVIDIA Tesla T4", true)]
    [InlineData("vulkan-native", "NVIDIA T4", true)]
    public void CaptureDeviceExcludesMetalAndSoftware(string backend, string device, bool expected)
        => Assert.Equal(expected, InteriorCutCostPerfGpuTests.IsCaptureDevice(backend, device));

    [Fact]
    public void EmptyFieldKeepsOptInWithoutCutting()
    {
        var scene = InteriorCutCostScene.Create();
        Assert.Null(scene.FieldFor(InteriorCutCostPerfGpuTests.Mode.ShadowsOff));
        Assert.Null(scene.FieldFor(InteriorCutCostPerfGpuTests.Mode.NoOptIn));
        Assert.False(scene.CutsFor(InteriorCutCostPerfGpuTests.Mode.ShadowsOff));
        Assert.False(scene.CutsFor(InteriorCutCostPerfGpuTests.Mode.NoOptIn));
        Assert.True(scene.CutsFor(InteriorCutCostPerfGpuTests.Mode.EmptyField));
        var field = Assert.IsType<KhaozEngine.Primitives.InteriorHeightField>(
            scene.FieldFor(InteriorCutCostPerfGpuTests.Mode.EmptyField));
        Assert.False(field.Texels.Span.ContainsAnyExcept((byte)0));
    }

    [Fact]
    public void VillageInteriorsCutTreePositionsAcrossTheScene()
    {
        var scene = InteriorCutCostScene.Create();
        Assert.Equal(3600, scene.Trees.Count);
        Assert.Equal(49, scene.Interiors.Count);
        Assert.All(scene.Trees, tree =>
        {
            Assert.InRange(tree.X, -250f, 250f);
            Assert.InRange(tree.Z, -250f, 250f);
        });
        Assert.True(scene.Trees.Max(t => t.X) - scene.Trees.Min(t => t.X) > 490f);
        Assert.True(scene.Trees.Max(t => t.Z) - scene.Trees.Min(t => t.Z) > 490f);
        Assert.True(scene.CutsFor(InteriorCutCostPerfGpuTests.Mode.VillageInteriors));
        var field = Assert.IsType<KhaozEngine.Primitives.InteriorHeightField>(
            scene.FieldFor(InteriorCutCostPerfGpuTests.Mode.VillageInteriors));
        Assert.True(field.Texels.Span.ContainsAnyExcept((byte)0));
        Assert.Contains(scene.Trees, t => field.Contains(new Vector3(t.X, 5f, t.Z)));
        Assert.Contains(scene.Trees, t => !field.Contains(new Vector3(t.X, 5f, t.Z)));
        Assert.All(scene.Interiors, interior =>
        {
            Vector2 centre = (interior.Outline[0] + interior.Outline[2]) * .5f;
            Assert.True(field.Contains(new Vector3(centre.X, 5f, centre.Y)));
        });
    }

    [Fact]
    public void AdvancingSunKeepsSuccessiveFramesDistinct()
    {
        var scene = InteriorCutCostScene.Create();
        Assert.Equal(0f, scene.SunAngleFor(0));
        Assert.Equal(.2f, scene.SunAngleFor(60), precision: 6);
        Assert.NotEqual(scene.SunAngleFor(0), scene.SunAngleFor(1));
        Assert.NotEqual(scene.SunAngleFor(839), scene.SunAngleFor(840));
    }
}

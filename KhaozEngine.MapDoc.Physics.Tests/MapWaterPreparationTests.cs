using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapWaterPreparationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SequentialWaterBodies_FitLiveWorldPeak(bool transformed)
    {
        using var fixture = Fixture(transformed);
        var source = NativeSceneFixtures.Open(fixture, false);
        long ceiling = transformed ? 16_000_000 : 1_000_000;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: ceiling));
        var acquired = MapSceneAcquisition.Run(source, Window(), admission);
        Assert.True(acquired.Scene is not null, string.Join("\n", acquired.Findings));
        long before = admission.RetainedBytes;
        var result = MapScopedWorldBuilder.Build(acquired.Scene, fixture.Assets, NativeWorldFixtures.Options());
        output.WriteLine($"World peak {admission.PeakBytes}, retained {admission.RetainedBytes}, ceiling {ceiling}");
        Assert.True(result.World is not null, result.Capacity?.Message ?? string.Join("\n", result.Findings));
        using var world = result.World;
        Assert.Equal(16, world.Water.Count);
        Assert.All(world.Water, b =>
        {
            Assert.Equal(MapWaterCompileStatus.Complete, b.Status);
            Assert.Equal(transformed ? -1.75f : -2f, b.DeepestBedY);
            Assert.Equal(2, Assert.Single(b.Regions).Triangles.Count);
        });
        Assert.InRange(admission.PeakBytes, 1, ceiling);
        world.Dispose();
        Assert.Equal(before, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void ChunkedBoundRead_ReleasesConsumedCompilers()
    {
        using var fixture = Fixture(false);
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(Window()).Scene!;
        var view = MapScopedSurfaces.CompleteEffective(scene.SurfaceView.EffectiveContext!);
        var footprint = fixture.Document.Surfaces.Patches.Values.SelectMany(p => p.Records).OfType<MapSpaceFootprint>().Single();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 160_000));
        using var active = admission.Enter();
        using var consumer = admission.BeginPreparation();
        var read = MapWaterBoundReads.Read(view, footprint, footprint.Lower,
            new(default, default), new(new(26, 1), new(7, 1)), new(MaxSourceFacesPerBound: 2), new(new()), upper: false);
        Assert.Equal(MapRefinementStatus.Complete, read.Status);
        Assert.Equal(364, read.Faces.Count);
        Assert.All(read.Faces, f => Assert.Equal(new MapExactValue(-2, 1), f.Triangle.A.Y));
        Assert.Equal(182, admission.PreparedFaces / 2);
        Assert.InRange(admission.PeakBytes, 1, 160_000);
        output.WriteLine($"Chunked read peak {admission.PeakBytes}, retained {admission.RetainedBytes}");
        Assert.True(admission.RetainedBytes > 0);
        Assert.Equal(0, admission.WorkspaceBytes);
        consumer.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void TransformedBoundReads_ReleaseLocalAndTransformedCompilers()
    {
        using var fixture = Fixture(true);
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(Window()).Scene!;
        var view = MapScopedSurfaces.CompleteEffective(scene.SurfaceView.EffectiveContext!);
        var footprint = fixture.Document.Surfaces.Patches.Values.SelectMany(p => p.Records).OfType<MapSpaceFootprint>().Single();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 256_000));
        using var active = admission.Enter();
        using var consumer = admission.BeginPreparation();
        var held = new List<MapWaterBoundRead>();
        for (int i = 0; i < 16; i++)
        {
            var read = MapWaterBoundReads.Read(view, footprint, footprint.Lower,
                new(new(i, 1), new(1, 1)), new(new(i + 1, 1), new(2, 1)), new(MaxSourceFacesPerBound: 128), new(new()), upper: false);
            Assert.True(read.Status == MapRefinementStatus.Complete, read.Detail);
            Assert.Equal(i == 0 ? 24 : 32, read.Faces.Count);
            Assert.All(read.Faces, f => Assert.Equal(new MapExactValue(-7, 4), f.Triangle.A.Y));
            held.Add(read);
        }
        Assert.Equal(504, held.Sum(r => r.Faces.Count));
        Assert.InRange(admission.PeakBytes, 1, 256_000);
        output.WriteLine($"Transformed reads peak {admission.PeakBytes}, retained {admission.RetainedBytes}");
        Assert.True(admission.RetainedBytes > 0);
        Assert.Equal(0, admission.WorkspaceBytes);
        consumer.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void RefusedWaterBuild_RestoresEarlierSceneInventory()
    {
        using var fixture = Fixture(false);
        var source = NativeSceneFixtures.Open(fixture, false);
        bool building = false;
        int compiles = 0;
        var admission = new MapSceneAdmission(new(), stage =>
        {
            if (building && stage == "compile" && ++compiles == 4)
                throw new MapSceneCapacityException("injected water refusal", 4, 3);
        });
        var acquired = MapSceneAcquisition.Run(source, Window(), admission);
        Assert.NotNull(acquired.Scene);
        long before = admission.RetainedBytes;
        building = true;
        var result = MapScopedWorldBuilder.Build(acquired.Scene, fixture.Assets, NativeWorldFixtures.Options());
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Null(result.World);
        Assert.Equal(new MapSceneCapacity("injected water refusal", 4, 3), result.Capacity);
        Assert.Equal(before, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    static MapSceneScope Window() => NativeSceneFixtures.WaterWindow(0, 26, 7) with { Channels = MapSceneChannels.All };

    static NativeSceneFixture Fixture(bool transformed)
    {
        var fixture = NativeSceneFixtures.Create("water-bent-border");
        var water = fixture.Document.Water!;
        var template = water.Bodies.Single();
        water.Bodies.Clear();
        water.Bodies.AddRange(Enumerable.Range(0, 16).Select(i => template with
        {
            Id = "pond-" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
            Domain = new(template.Domain.Frame, [new(i, 1, i + 1, 2)], []),
        }));
        if (transformed)
            fixture.Document.NativeResourceTransforms = [new(MapPrefabChildKind.Surface, "wet-floor", new(0, .25, 0, 0, 1), new string('a', 64))];
        return fixture;
    }
}

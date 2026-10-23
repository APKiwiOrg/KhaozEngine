using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldInteriorCutCaptureTests
{
    [Fact]
    public void OptionsSeedTheViewAndRuntimeSettersRemainIndependent()
    {
        var meshes = new InteriorTestMeshes();
        var options = new TileWorldViewOptions { InteriorMeshes = meshes, RoofMode = RoofVisibility.AlwaysHidden };
        using var view = new TileWorldView(new RecordingTileWorldScene(), new TileWorldDocument(),
            TileWorldCatalogs.Greybox(), new GreyboxMeshResolver(), options);
        Assert.Same(meshes, view.InteriorMeshes);
        Assert.Equal(RoofVisibility.AlwaysHidden, view.RoofMode);
        view.InteriorMeshes = null;
        view.RoofMode = RoofVisibility.AlwaysVisible;
        Assert.Same(meshes, options.InteriorMeshes);
        Assert.Equal(RoofVisibility.AlwaysHidden, options.RoofMode);
    }

    [Fact]
    public void SequencePublishesTheFieldBeforeItsFirstDraw()
    {
        using var rig = new MotionTestScene();
        TileWorldDocument doc = InteriorCaptureWorld.Create();
        var sequence = new TileWorldSequenceView(rig.Scene, doc, TileWorldCatalogs.Greybox(),
            new GreyboxMeshResolver(), InteriorCaptureWorld.Options(),
            new Vector3(10.5f, 6f, 0f), InteriorCaptureWorld.Focus, 80, 60, observer: null);
        InteriorHeightField field = Assert.IsType<InteriorHeightField>(rig.Scene.InteriorCut);
        Assert.True(field.Contains(new Vector3(10.5f, 1f, -10.5f)));
        Assert.False(field.Contains(new Vector3(16.5f, 1f, -10.5f)));
        rig.Scene.Begin();
        sequence.Draw(0, null);
        Assert.True(Assert.Single(rig.Scene.QueuedInstancesForTests, i => i.World.M41 == 10.5f && i.World.M42 == 0f)
            .CutInsideInteriors);
        Assert.False(Assert.Single(rig.Scene.QueuedInstancesForTests, i => i.World.M41 == 16.5f)
            .CutInsideInteriors);
        Assert.Same(field, rig.Scene.InteriorCut);
    }

    [Theory]
    [InlineData(RoofVisibility.AlwaysHidden, 0)]
    [InlineData(RoofVisibility.AlwaysVisible, 1)]
    public void SequenceOptionsReachRoofDrawing(RoofVisibility mode, int visibleRoofs)
    {
        using var rig = new MotionTestScene();
        TileWorldDocument doc = InteriorCaptureWorld.Create();
        var sequence = new TileWorldSequenceView(rig.Scene, doc, TileWorldCatalogs.Greybox(),
            new GreyboxMeshResolver(), new TileWorldViewOptions { RoofMode = mode },
            new Vector3(10.5f, 6f, 0f), InteriorCaptureWorld.Focus, 80, 60, observer: null);
        rig.Scene.Begin();
        sequence.Draw(0, null);
        Assert.Equal(visibleRoofs, rig.Scene.QueuedInstancesForTests.Count(i =>
            i.World.M42 == doc.PlaneHeight && !i.ShadowOnly));
    }

    [Fact]
    public void SequenceSettlesARecentreWithinTheSameRegionBeforeDrawing()
    {
        using var rig = new MotionTestScene();
        TileWorldDocument doc = InteriorCaptureWorld.Create();
        doc.TileSize = 2f;
        var initial = new Vector3(21f, 0f, -21f);
        var sequence = new TileWorldSequenceView(rig.Scene, doc, TileWorldCatalogs.Greybox(),
            new GreyboxMeshResolver(2f), InteriorCaptureWorld.Options(), initial + new Vector3(0, 6, 10),
            initial, 80, 60, observer: null);
        InteriorHeightField first = Assert.IsType<InteriorHeightField>(rig.Scene.InteriorCut);
        var next = new Vector3(91f, 0f, -21f);
        rig.Scene.Begin();
        sequence.Draw(0, _ => (next + new Vector3(0, 6, 10), next));
        InteriorHeightField moved = Assert.IsType<InteriorHeightField>(rig.Scene.InteriorCut);
        Assert.NotSame(first, moved);
        Assert.Equal(InteriorWindow.Centred(new Vector2(91f, -21f), .25f, 2048), moved.Window);
    }
}

internal static class InteriorCaptureWorld
{
    internal static readonly Vector3 Focus = new(10.5f, 0f, -10.5f);

    internal static TileWorldDocument Create()
    {
        var doc = new TileWorldDocument();
        doc.GetOrCreateRegion(default);
        for (int z = 10; z < 12; z++)
            for (int x = 10; x < 12; x++) doc.SetSettings(x, z, 0, TileSettings.Indoors);
        doc.AddObject("roof_flat", 10, 10, 1, 0);
        doc.AddObject("tree", 10, 10, 0, 0);
        doc.AddObject("tree", 16, 10, 0, 0);
        return doc;
    }

    internal static TileWorldViewOptions Options(bool cut = true) => new()
    {
        InteriorMeshes = new InteriorTestMeshes(),
        RoofMode = RoofVisibility.AlwaysHidden,
        PropLayers = [new TilePropLayerDefinition
        {
            Id = "trees", ArchetypeIds = new HashSet<string> { "tree" },
            DrawRadius = 192f, LodDistance = 64f, CutInsideInteriors = cut,
        }],
    };
}

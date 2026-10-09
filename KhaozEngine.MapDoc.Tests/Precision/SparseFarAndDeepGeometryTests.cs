using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public class SparseFarAndDeepGeometryTests
{
    static bool Within(double a, double b) => Math.Abs(a - b) <= 0.0001;

    [Theory, InlineData(50000, 1500, 250), InlineData(-50000, -1500, -250), InlineData(50000, 1506, 251), InlineData(-50000, -1506, -251)]
    public void SparseFarAndDeepGeometry_PreservesLocalCoordinates(int baseCm, long slot, short frameIndex)
    {
        MapDocument near = PrecisionFixtures.Probe(0, 0, baseCm), far = PrecisionFixtures.Probe(slot, slot, baseCm);
        var frame = new WorldFrame(frameIndex, frameIndex);
        MapFrameMesh a = MapFrameLocal.ToFrame(PrecisionFixtures.Compile(near), WorldFrame.Origin), b = MapFrameLocal.ToFrame(PrecisionFixtures.Compile(far), frame);
        Assert.Equal(a.LocalPositions, b.LocalPositions);
        Assert.Equal(a.Faces.Select(f => f.Normal), b.Faces.Select(f => f.Normal));
        var local = new Vector3(0.4f, baseCm / 100f + 0.5f, 0.6f);
        foreach (var (doc, f) in new[] { (near, WorldFrame.Origin), (far, frame) })
        {
            MapSupportResult r = PrecisionFixtures.Select(doc, f, local, up: 0.5f, down: 1f);
            Assert.Equal(MapSupportStatus.Supported, r.Status);
            Assert.True(Within(r.WorldY, PrecisionFixtures.ExactQueryHeight(doc, f, local).ToDouble()));
        }
        Assert.Contains("frame radius", Assert.Throws<MapDocumentException>(() => MapFrameLocal.ToFrame(PrecisionFixtures.Compile(far), WorldFrame.Origin)).Message);
    }
    [Theory, InlineData(0.8f, 563.5f), InlineData(1.2f, 563.5f), InlineData(0.8f, -563.5f), InlineData(1.2f, -563.5f)]
    public void RigidLocalFloor_ComposesTheSameInNearAndFarFrames(float scale, float y)
    {
        MapCompiledPatch deck = PrecisionFixtures.Compile(PrecisionFixtures.Deck());
        var frame = new WorldFrame(250, 250);
        var farPose = new MapTransform(new Vector3(32010.25f, y, 31996.5f), 0.371f, scale);
        MapFrameMesh a = MapFrameLocal.CompileInFrame(deck, new MapTransform(new Vector3(10.25f, y, -3.5f), 0.371f, scale), WorldFrame.Origin);
        MapFrameMesh b = MapFrameLocal.CompileInFrame(deck, farPose, frame);
        Assert.Equal(a.LocalPositions, b.LocalPositions);
        Assert.Contains(new Vector3(10.25f, y, -3.5f), a.LocalPositions);                 // the deck's local origin vertex
        var reference = PrecisionFixtures.ReferenceInFrame(deck, farPose, frame);
        for (int i = 0; i < reference.Count; i++)
            Assert.True(Within(b.LocalPositions[i].X, reference[i].X) && Within(b.LocalPositions[i].Y, reference[i].Y) && Within(b.LocalPositions[i].Z, reference[i].Z));
    }
    [Fact]
    public void ProbeAt640Metres_MeetsTheTargetAndTheNamedRiskIsRecorded()
    {
        MapDocument probe = PrecisionFixtures.Probe(1500, 1500, 50000);
        var frame = new WorldFrame(250, 250);
        var local = new Vector3(0.4f, 639.9f, 0.6f);
        MapSupportResult r = PrecisionFixtures.Select(probe, frame, local, up: 0f, down: 141f);
        Assert.True(Within(r.WorldY, PrecisionFixtures.ExactQueryHeight(probe, frame, local).ToDouble()));
        Assert.Equal(0.00006103515625f, MathF.BitIncrement(640f) - 640f);
        Assert.Equal(0.000030517578125f, (MathF.BitIncrement(640f) - 640f) / 2);
        Assert.True(Math.Abs((32000.01f - 32000f) - 0.01) <= 0.001);
    }

    [Fact]
    public void SparseFarStrip_PreservesLocalCoordinates()
    {
        MapCompiledStrip near = PrecisionFixtures.Strip(0), far = PrecisionFixtures.Strip(1500);
        var frame = new WorldFrame(250, 250);
        MapFrameMesh a = MapFrameLocal.ToFrame(near, WorldFrame.Origin), b = MapFrameLocal.ToFrame(far, frame);
        Assert.Equal(a.LocalPositions, b.LocalPositions);
        Assert.Equal(a.Faces.Select(f => f.Normal), b.Faces.Select(f => f.Normal));
        Assert.Contains("frame radius", Assert.Throws<MapDocumentException>(() => MapFrameLocal.ToFrame(far, WorldFrame.Origin)).Message);
    }
}

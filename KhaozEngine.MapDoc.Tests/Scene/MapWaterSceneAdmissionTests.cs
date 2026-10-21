using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapWaterSceneAdmissionTests
{
    [Theory]
    [InlineData("domain")]
    [InlineData("cells")]
    [InlineData("traversal")]
    [InlineData("read")]
    [InlineData("triangles")]
    public void SceneByteRefusal_PreservesMeterAndAdmissionAndAllowsRetry(string operation)
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var meter = new MapWaterWorkMeter(new(MaxBoundFaces: 1));
        var before = meter.Work;
        var refused = new MapSceneAdmission(new(MaxPreparedBytes: 0));
        using (refused.Enter()) Assert.Throws<MapSceneCapacityException>(Charge);
        Assert.Equal(before, meter.Work);
        Assert.Equal(0, refused.RetainedBytes);
        Assert.Equal(0, refused.PreparedFaces);
        // A rejected bound read must not consume its private face count either.
        var accepted = new MapSceneAdmission(new());
        using (accepted.Enter()) Charge();
        Assert.True(accepted.RetainedBytes > 0);

        void Charge()
        {
            if (operation == "domain") meter.ChargeDomain(fixture.Document.Water!.Bodies[0].Domain);
            else if (operation == "cells") meter.ChargeFootprintCells(1);
            else if (operation == "traversal") meter.ChargeBoundTraversal(BigInteger.One, BigInteger.One);
            else if (operation == "read") meter.ChargeRead(1);
            else meter.ChargeTriangles(1);
        }
    }

    [Fact]
    public void DomainCharge_CoversWideNarrowAndCachedTriangulationBuffers()
    {
        using var fixture = NativeSceneFixtures.Create("water-bent-one");
        var meter = new MapWaterWorkMeter(new());
        var admission = new MapSceneAdmission(new());
        using (admission.Enter())
        {
            var doc = fixture.Document.Water!.Bodies[0].Domain;
            meter.ChargeDomain(doc);
            var domain = MapWaterDomain.Compile(doc);
            long vertices = domain.Pieces.Sum(p => (long)p.Ring.Count);
            long corners = Enumerable.Range(0, domain.Pieces.Count).Sum(i =>
                domain.ConvexParts(i, meter.Pairs).Sum(p => (long)p.Count));
            Assert.True(corners > vertices); // The bent ring needs a retained triangulation, not one convex alias.
            long buffers = vertices * (Unsafe.SizeOf<MapWideXz>() + Unsafe.SizeOf<MapExactXz>()) +
                corners * Unsafe.SizeOf<MapWideXz>() + domain.Pieces.Count * Unsafe.SizeOf<MapWaterPolygons.Bounds>();
            Assert.True(admission.RetainedBytes >= buffers,
                $"Admitted {admission.RetainedBytes} bytes for at least {buffers} bytes of retained value buffers.");
        }
    }

    [Fact]
    public void BoundRead_ReservesCopiesWithoutDoubleCountingSurfaceCompilation()
    {
        var patch = PrefabFixtures.Patch("floor", 2, 2, 0);
        var surface = PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor);
        var meter = new MapWaterWorkMeter(new());
        var admission = new MapSceneAdmission(new(MaxPreparedFaces: 10));
        using (admission.Enter())
        {
            meter.ChargeRead(8);
            Assert.Equal(0, admission.PreparedFaces);
            Assert.True(admission.RetainedBytes >= 4L * 8 * Unsafe.SizeOf<MapBoundFace>());
            Assert.Equal(8, MapSurfaceCompiler.Compile(surface, patch).Faces.Count);
            Assert.Equal(8, admission.PreparedFaces);
            meter.ChargeTriangles(2); // New emitted water faces are distinct from the eight source faces.
            Assert.Equal(10, admission.PreparedFaces);
        }
        Assert.Null(MapSceneAdmission.Current);
    }
}

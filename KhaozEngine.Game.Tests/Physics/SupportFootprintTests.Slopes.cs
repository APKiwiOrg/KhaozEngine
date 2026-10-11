using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(FootprintPartition.Boxes)]
    [InlineData(FootprintPartition.Hulls)]
    [InlineData(FootprintPartition.Compound)]
    [InlineData(FootprintPartition.Mesh)]
    public void InstalledSlopedFacesResolveAcrossPartitions(FootprintPartition partition)
    {
        foreach (float slope in new[] { 50f, 60f, 70f })
        {
            using var scene = SupportFootprintScenes.HiddenCrate(partition, slope: slope);
            var request = Request();
            foreach (SupportPolygon polygon in CapturedPolygons(scene, request))
            {
                OracleFace face = scene.Faces.Find(f => f.Static == polygon.Static && f.ElementId == polygon.ElementId)!;
                FootprintOracleMember? oracle = SupportFootprintOracle.Polygon(face, request);
                GeometrySign measured = SupportFootprintPolygon.Measure(polygon, request, out var member);
                Assert.True(measured == (oracle is null ? GeometrySign.Negative : GeometrySign.Positive),
                    $"{partition} slope={slope} {polygon.Static}/{polygon.ElementId} {measured}");
                if (oracle is not null && oracle.Normal.Y > 0 && oracle.HasDiscMaximum) CheckMaximum(member, oracle);
            }
        }
    }

    static List<SupportPolygon> CapturedPolygons(SupportFootprintScene scene, SupportFootprintRequest request)
    {
        var simulation = (BepuPhysics.Simulation)typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene.World)!;
        var handles = (Dictionary<int, (BepuPhysics.StaticHandle Handle, TypedIndex Shape)>)typeof(BepuPhysicsWorld)
            .GetField("_handles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene.World)!;
        var polygons = new List<SupportPolygon>();
        var pool = new SupportPolygonPool();
        var scratch = new CapsuleFeatureCaptureScratch();
        var leaves = new List<CapsuleFeaturePolyhedron>();
        Assert.True(SupportFootprintBounds.TryCreate(request, out var bounds));
        foreach (var (owner, installed) in handles)
        {
            simulation.Statics.GetDescription(installed.Handle, out StaticDescription description);
            if (SupportNeighborhoodMesh.IsMesh(description.Shape))
                Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodMesh.Polygons(simulation,
                    description.Shape, description.Pose, new StaticHandle(owner), FeaturePoint.Exact(new Vector3(0, bounds.Minimum.Y, 0)),
                    FeaturePoint.Exact(new Vector3(0, bounds.Maximum.Y, 0)),
                    [bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z],
                    [bounds.Maximum.X, bounds.Maximum.Y, bounds.Maximum.Z], GeometryInterval.Exact(1), [], pool, polygons));
            else
            {
                scratch.Reset();
                Assert.Equal(CapsuleFeatureStatus.Complete, SupportNeighborhoodPolyhedra.Capture(simulation,
                    description.Shape, description.Pose, scratch, leaves));
                foreach (var leaf in leaves) SupportNeighborhoodPolyhedra.Polygons(new StaticHandle(owner), leaf, pool, polygons);
            }
        }
        return polygons;
    }
}

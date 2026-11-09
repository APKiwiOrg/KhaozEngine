using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Physics;

public class StaticEvidenceSpatialTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallApertureVisitsOnlyLocalOwnersAndExpandsOnlySelectedTriangles(bool exclude)
    {
        // #1753. Ground exclusions used to run after every triangle of the ground was expanded.
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var ground = world.AddStatic(Strip(2048), Pose.Identity);
        for (int i = 0; i < 256; i++)
            world.AddStatic(new BoxShape(Vector3.One), Pose.At(new(10000 + i * 4, 0, 0)));
        using var view = world.CreateQueryViewExcludingStatics(exclude ? [ground] : []);
        using var lease = world.AcquireQueryReadLease();
        using var work = new StaticEvidenceDiagnostics();
        int faces = 0;
        var status = ((IPhysicsStaticEvidence)view).CaptureStaticEvidence(lease,
            new(0.25f, -1, 0.25f), new(0.75f, 1, 0.75f), 8, Visit);
        output.WriteLine($"exclude={exclude}, owners={work.OwnersVisited}, triangles={work.TrianglesExpanded}, faces={faces}");
        Assert.Equal(PhysicsEvidenceStatus.Complete, status);
        Assert.Equal(exclude ? 0 : 1, faces);
        Assert.Equal(1, work.OwnersVisited);
        Assert.Equal(exclude ? 0 : 1, work.TrianglesExpanded);
        bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> vertices)
        {
            Assert.Equal(ground, face.Static);
            Assert.Equal(PhysicsEvidenceKind.Triangle, face.Kind);
            Assert.Equal(3, vertices.Length);
            faces++;
            return true;
        }
    }

    internal static TriangleMeshShape Strip(int triangles)
    {
        var vertices = new Vector3[triangles * 3];
        var indices = new int[vertices.Length];
        for (int i = 0; i < triangles; i++)
        {
            vertices[i * 3] = new(i % 64 * 2, 0, i / 64 * 2);
            vertices[i * 3 + 1] = new(i % 64 * 2, 0, i / 64 * 2 + 1);
            vertices[i * 3 + 2] = new(i % 64 * 2 + 1, 0, i / 64 * 2 + 1);
        }
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        return new(vertices, indices);
    }
}

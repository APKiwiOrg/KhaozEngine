using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void SourcePredicatesReuseOnlyTheUnchangedCoefficientMap()
    {
        SupportPolygon polygon = Polygon([new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1)], out _);
        Assert.True(InstalledPoseOperator.TryCreateSupport(new BepuPhysics.RigidPose(Vector3.Zero, Quaternion.Identity), out var map));
        polygon.BeginSource(map);
        for (int i = 0; i < polygon.SourceVertices.Length; i++)
            polygon.SetSource(i, new((float)polygon.Vertices[i].X.Value, 0, (float)polygon.Vertices[i].Z.Value));
        var shared = new SupportFootprintSourcePredicates();
        int setups = 0;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (phase == ContactAllocationPhase.SupportSourceSetup && entering) setups++;
        });
        try
        {
            Assert.Equal(new SupportFootprintSourcePredicates().NormalComponent(polygon, 1), shared.NormalComponent(polygon, 1));
            setups = 0;
            Assert.Equal(GeometrySign.Positive, shared.NormalSquared(polygon));
            Assert.Equal(GeometrySign.Zero, shared.NormalComponent(polygon, 0));
            Assert.True(shared.HorizontalPlaneHeight(polygon, out var height));
            Assert.Equal(GeometryInterval.Exact(0), height);
            Assert.Equal(0, setups);

            Assert.True(InstalledPoseOperator.TryCreateSupport(new BepuPhysics.RigidPose(Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.65f)), out var rotated));
            polygon.BeginSource(rotated);
            Assert.Equal(new SupportFootprintSourcePredicates().NormalComponent(polygon, 0), shared.NormalComponent(polygon, 0));
            Assert.Equal(new SupportFootprintSourcePredicates().NormalSquared(polygon), shared.NormalSquared(polygon));
            polygon.BeginSource(map);
            Assert.Equal(GeometrySign.Zero, shared.NormalComponent(polygon, 0));
            Assert.Equal(GeometrySign.Positive, shared.NormalSquared(polygon));
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
    }
}

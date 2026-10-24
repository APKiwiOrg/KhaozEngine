using System;
using System.Linq;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using V = KhaozEngine.Tests.Physics.InstalledPoseOracle.V;

namespace KhaozEngine.Tests.Physics;

public sealed partial class ConvexPlanarPenetrationTests
{
    static void AssertSupportProvenance(ConvexPenetrationCertificate e, ConvexOracleTarget snapshot)
    {
        R Rational(Q value)
        {
            Assert.True(value.Rational);
            return new(value.A.Numerator, value.A.Denominator);
        }
        V Copy(P point) => new(Rational(point.X), Rational(point.Y), Rational(point.Z));
        V normal = Copy(e.Direction), target = Copy(e.Target + e.Attained.Feet);
        R minimum = V.Dot(snapshot.Vertices[0], normal);
        foreach (var point in snapshot.Vertices)
            if (V.Dot(point, normal) < minimum) minimum = V.Dot(point, normal);
        Assert.Contains(target, snapshot.Vertices);
        Assert.Equal(minimum, V.Dot(target, normal));
        bool Exposed(int index) => V.Dot(snapshot.Vertices[index], normal) == minimum;
        bool face = snapshot.Faces.Any(f => f.All(Exposed));
        bool edge = snapshot.Faces.Any(f => Enumerable.Range(0, f.Length)
            .Any(i => Exposed(f[i]) && Exposed(f[(i + 1) % f.Length])));
        Assert.Equal(face ? ConvexTargetFeature.Face : edge ? ConvexTargetFeature.Edge : ConvexTargetFeature.Vertex,
            e.Contact.TargetFeature);

        // Check the reported raw local id against independently captured exposed vertices.
        var source = e.Initial.Source; int id = e.Contact.LocalFeatureId;
        int[] indices;
        switch (e.Contact.TargetFeature)
        {
            case ConvexTargetFeature.Face:
                Assert.InRange(id, 0, source.FaceCount - 1);
                indices = Enumerable.Range(0, source.InteriorFaceSize(id)).Select(i => source.FaceVertex(id, i)).ToArray();
                break;
            case ConvexTargetFeature.Edge:
                id -= source.FaceCount;
                Assert.InRange(id, 0, source.EdgeCount - 1);
                var ends = source.Edge(id); indices = [ends.A, ends.B];
                break;
            default:
                id -= source.FaceCount + source.EdgeCount;
                Assert.InRange(id, 0, source.VertexCount - 1);
                indices = [id];
                break;
        }
        foreach (int index in indices)
        {
            V point = Copy(source.Vertex(index));
            Assert.Contains(point, snapshot.Vertices);
            Assert.Equal(minimum, V.Dot(point, normal));
        }

        // A feature label describes this support point, not the ideal shortest exit's point.
        var shape = e.Attained.Request.Shape;
        P q = e.Query;
        Q radial = q.X * q.X + q.Z * q.Z, radius = Q.From(shape.Radius);
        Q lower = Q.From(shape.LowerY), ring = Q.From(shape.RingY), upper = Q.From(shape.UpperY);
        switch (e.Contact.QueryFeature)
        {
            case ConvexShellFeature.Apex:
                Assert.Equal(0, radial.Sign); Assert.Equal(0, (q.Y - lower).Sign);
                break;
            case ConvexShellFeature.LowerCone:
                Assert.True((q.Y - lower).Sign > 0 && (ring - q.Y).Sign > 0);
                Q height = ring - lower, y = q.Y - lower;
                Assert.Equal(0, (radial * height * height - radius * radius * y * y).Sign);
                break;
            case ConvexShellFeature.Shoulder:
                Assert.Equal(0, (q.Y - ring).Sign); Assert.Equal(0, (radial - radius * radius).Sign);
                break;
            case ConvexShellFeature.Cylinder:
                Assert.True((q.Y - ring).Sign > 0 && (upper - q.Y).Sign > 0);
                Assert.Equal(0, (radial - radius * radius).Sign);
                break;
            case ConvexShellFeature.Rim:
                Assert.Equal(0, (q.Y - upper).Sign); Assert.Equal(0, (radial - radius * radius).Sign);
                break;
            case ConvexShellFeature.HeadDisc:
                Assert.Equal(0, (q.Y - upper).Sign); Assert.True((radial - radius * radius).Sign < 0);
                break;
            default:
                Assert.Fail("A support witness must name an original shell boundary feature.");
                break;
        }
    }
}

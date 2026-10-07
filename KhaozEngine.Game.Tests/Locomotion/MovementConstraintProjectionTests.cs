using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Displacement candidates only. Every returned candidate still requires a complete solid/water trace.
public class MovementConstraintProjectionTests
{
    delegate MovementAvailability ProjectCall(Vector3 requested, ReadOnlySpan<Vector3> normals, out Vector3 projected);

    [Fact]
    public void NoConstraintsPreserveTheRequest() =>
        Known(new Vector3(1f, 2f, 3f), [], new Vector3(1f, 2f, 3f));

    [Fact]
    public void InwardWallRemovesOnlyItsBlockedComponent() =>
        Known(new Vector3(-2f, 1f, 3f), [Vector3.UnitX], new Vector3(0f, 1f, 3f));

    [Fact]
    public void MotionAwayFromTheWallIsUnchanged() =>
        Known(new Vector3(2f, 1f, 3f), [Vector3.UnitX], new Vector3(2f, 1f, 3f));

    [Fact]
    public void TangencyKeepsTheTangentComponent() =>
        Known(new Vector3(0f, 0f, 2f), [Vector3.UnitX], new Vector3(0f, 0f, 2f));

    [Fact]
    public void TwoWallsKeepOnlyTheSharedFeasibleDirection() =>
        Known(new Vector3(-2f, 3f, -1f), [Vector3.UnitX, Vector3.UnitZ], new Vector3(0f, 3f, 0f));

    [Fact]
    public void OpposingPlanesPreserveTheirCommonTangentPlane() =>
        Known(new Vector3(1f, 2f, 3f), [Vector3.UnitX, -Vector3.UnitX], new Vector3(0f, 2f, 3f));

    [Fact]
    public void AClosedCornerStopsTheInwardRequest() =>
        Known(new Vector3(-1f), [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ], Vector3.Zero);

    [Fact]
    public void NonorthogonalPlaneProjectsWithoutChoosingAnAxisWall() =>
        Known(new Vector3(1f, 2f, -2f), [Vector3.UnitX, new Vector3(0.6f, 0f, 0.8f)],
            new Vector3(1.6f, 2f, -1.2f));

    [Fact]
    public void ReversingContactOrderKeepsTheSameCandidate()
    {
        Vector3[] normals = [Vector3.UnitX, new Vector3(0.6f, 0f, 0.8f), Vector3.UnitY];
        Vector3 requested = new(1f, -2f, -2f);
        Vector3 a = Known(requested, normals, new Vector3(1.6f, 0f, -1.2f));
        Array.Reverse(normals);
        Vector3 b = Known(requested, normals, a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RepeatedIdenticalNormalsDoNotConsumeDistinctPlaneCapacity() =>
        Known(new Vector3(-1f, 0f, 2f), Enumerable.Repeat(Vector3.UnitX, 256).ToArray(), new Vector3(0f, 0f, 2f));

    [Fact]
    public void InvalidNormalsRefuseWithoutACandidate()
    {
        ProjectCall project = Bind();
        foreach (Vector3 normal in new[] { Vector3.Zero, new Vector3(float.NaN, 0f, 0f), Vector3.UnitX * 2f })
        {
            Vector3 result = new(123f);
            Assert.Equal(MovementAvailability.Invalid, project(Vector3.One, [normal], out result));
            Assert.Equal(Vector3.Zero, result);
        }
    }

    [Fact]
    public void ExcessInputOrDistinctNormalsRefusesInsteadOfProjectingAPrefix()
    {
        ProjectCall project = Bind();
        Vector3[] normals = Enumerable.Repeat(Vector3.UnitX, 16384 + 256 + 1).ToArray();
        Vector3 result = new(123f);
        Assert.Equal(MovementAvailability.CapacityExceeded, project(-Vector3.UnitX, normals, out result));
        Assert.Equal(Vector3.Zero, result);
        Vector3[] distinct = Enumerable.Range(0, 65)
            .Select(i => Vector3.Normalize(new Vector3(1f, 0f, i * 0.01f))).ToArray();
        result = new Vector3(123f);
        Assert.Equal(MovementAvailability.CapacityExceeded, project(-Vector3.UnitX, distinct, out result));
        Assert.Equal(Vector3.Zero, result);
    }

    static Vector3 Known(Vector3 requested, Vector3[] normals, Vector3 expected)
    {
        ProjectCall project = Bind();
        Assert.Equal(MovementAvailability.Known, project(requested, normals, out Vector3 candidate));
        Assert.InRange(Vector3.Distance(candidate, expected), 0f, 0.00001f);
        foreach (Vector3 normal in normals)
            Assert.True(Vector3.Dot(normal, candidate) >= -0.000001f, "Candidate lost an active constraint.");
        Assert.True(candidate.LengthSquared() <= requested.LengthSquared() + 0.00001f);
        return candidate;
    }

    static ProjectCall Bind()
    {
        Type? type = typeof(MoveState).Assembly.GetType("KhaozEngine.Locomotion.MovementConstraintProjection");
        Assert.NotNull(type);
        MethodInfo? method = type.GetMethod("TryProject", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.CreateDelegate<ProjectCall>();
    }
}

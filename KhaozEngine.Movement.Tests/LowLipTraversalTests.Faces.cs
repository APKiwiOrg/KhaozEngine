using System;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public partial class LowLipTraversalTests
{
    // One downward capsule sweep and at most one downward ray per fixed case. These observations compare the
    // existing query results with known fixture geometry. Corner rays are boundary observations, not proof that a
    // runtime fallback may accept them. Interior controls do not certify a backend-wide numerical domain.
    [Theory]
    [InlineData("box", -0.145f, false, 0)]
    [InlineData("box", -0.145f, true, 0)]
    [InlineData("box", -0.135f, false, 0)]
    [InlineData("box", -0.135f, true, 0)]
    [InlineData("box", -0.128f, false, 0)]
    [InlineData("box", -0.128f, true, 0)]
    [InlineData("mesh", -0.145f, false, 0)]
    [InlineData("mesh", -0.145f, true, 0)]
    [InlineData("mesh", -0.135f, false, 0)]
    [InlineData("mesh", -0.135f, true, 0)]
    [InlineData("mesh", -0.128f, false, 0)]
    [InlineData("mesh", -0.128f, true, 0)]
    [InlineData("low-box", -0.125f, true, 0)]
    [InlineData("high-box", -0.135f, true, 0)]
    [InlineData("box-interior", 0.5f, true, 0)]
    [InlineData("mesh-interior", 0.5f, true, 0)]
    [InlineData("slope", 0f, true, 0)]
    [InlineData("dome", 0f, true, 0)]
    [InlineData("wall", -0.145f, true, 0)]
    [InlineData("roof", -0.125f, true, 0)]
    [InlineData("box", -0.135f, true, 1)]
    [InlineData("mesh", -0.135f, true, 1)]
    [InlineData("box", -0.135f, true, 2)]
    [InlineData("mesh-interior", 0.5f, true, 2)]
    public void ExistingQueriesObserveCornerAndInteriorFaces(string shape, float x, bool excludeFloor,
        int translation)
    {
        Vector3 offset = translation switch
        {
            0 => Vector3.Zero,
            1 => new Vector3(45f, 1.4825f, -97.75f),
            2 => new Vector3(1024f, 100f, -1024f),
            _ => throw new ArgumentOutOfRangeException(nameof(translation)),
        };
        if (shape == "dome") x = -(2f + Tuning.CapsuleRadius) * MathF.Sqrt(1f - 0.85f * 0.85f);
        using var world = new BepuPhysicsWorld();
        StaticHandle floor = world.AddStatic(new BoxShape(new Vector3(8f, 0.1f, 8f)),
            Pose.At(offset + new Vector3(0f, -0.1f, 0f)));
        (StaticHandle target, float? top, float expectedNormalY) = AddFaceProbeShape(world, shape, offset);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics(excludeFloor ? [floor] : []);
        Assert.Same(world, view.SourceWorld);
        Assert.Equal(Vector3.Zero, view.Origin);

        CapsuleShape capsule = CharacterMovement.CapsuleFor(Tuning);
        Vector3 centre = offset + new Vector3(x, Tuning.CapsuleHalfHeight, Row);
        float probeStart = centre.Y + 2f * Tuning.CapsuleHalfHeight;
        // The unchanged LowPropSupport query starts here and ends 1 cm below the terrain resting centre.
        float maxProbe = probeStart - (offset.Y + Tuning.CapsuleHalfHeight) + 0.01f;
        bool swept = view.SweepCapsule(capsule, Pose.At(new Vector3(centre.X, probeStart, centre.Z)),
            -Vector3.UnitY, maxProbe, out SweepHit hit);
        float cosMax = MathF.Cos(Tuning.MaxSlopeRadians);
        _restingOutput.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"face shape={shape}, x={x:R}, excludeFloor={excludeFloor}, offset={offset}, swept={swept}, " +
            $"body={hit.Body}, point={hit.Point}, normal={hit.Normal}, distance={hit.Distance:R}, target={target}"));
        if (shape is "wall" or "roof")
        {
            // Both are initial-overlap controls, not ordinary wall approach. Their no-normal result is a refusal,
            // not a geometric-face witness. Check the actual body rather than treating any miss as this control.
            Assert.True(swept);
            Assert.Equal<StaticHandle?>(target, hit.Body);
            Assert.Equal(0f, hit.Distance);
            Assert.Equal(Vector3.Zero, hit.Normal);
            return;
        }
        bool corner = shape is "box" or "mesh" or "low-box" or "high-box";
        Assert.True(swept, "the sweep did not reach the known fixture contact inside its range");
        Assert.Equal<StaticHandle?>(target, hit.Body);
        Assert.True(hit.Normal.Y >= cosMax, "the support contact is not in the walkable band");

        Vector3 rayStart = new(hit.Point.X, probeStart, hit.Point.Z);
        // This is a measurement query down to the observed contact, not a guessed horizontal inset.
        float rayLength = probeStart - hit.Point.Y + 0.01f;
        Assert.True(float.IsFinite(rayLength) && rayLength > 0f);
        bool rayFound = view.Raycast(rayStart, -Vector3.UnitY, rayLength, out RayHit face);
        float heightError = rayFound ? MathF.Abs(face.Point.Y - hit.Point.Y) : float.PositiveInfinity;
        float rise = probeStart - hit.Distance - centre.Y;
        float dx = centre.X - offset.X;
        float analyticNormalY = corner ? MathF.Sqrt(1f - dx * dx / (capsule.Radius * capsule.Radius)) : expectedNormalY;
        float analyticRise = corner
            ? top!.Value - offset.Y - capsule.Radius + MathF.Sqrt(capsule.Radius * capsule.Radius - dx * dx)
            : float.NaN;
        _restingOutput.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"ray found={rayFound}, sameBody={rayFound && face.Body == hit.Body}, point={face.Point}, normal={face.Normal}, " +
            $"heightError={heightError:R}, edgeOffsetX={hit.Point.X - offset.X:R}, centreRise={rise:R}, " +
            $"analyticRise={analyticRise:R}, contactNormalY={hit.Normal.Y:R}, analyticNormalY={analyticNormalY:R}, " +
            $"sweepFlat={hit.Normal.Y >= 0.9f}"));
        if (corner)
        {
            Assert.InRange(MathF.Abs(rise - analyticRise), 0f, 0.0001f);
            if (shape != "mesh")
            {
                Assert.InRange(MathF.Abs(hit.Normal.Y - analyticNormalY), 0f, 0.0001f);
                Assert.Equal(analyticNormalY >= 0.9f, hit.Normal.Y >= 0.9f);
            }
            if (shape is "low-box" or "high-box") Assert.False(rise > 0f && rise <= 0.1f);
            else Assert.InRange(rise, float.Epsilon, 0.1f);
            // A ray exactly on the corner can hit the top, miss, or reach the excluded/included floor depending
            // on the sign of its reported point's error. None of those outcomes establishes a safe fallback.
            return;
        }
        Assert.True(rayFound, "the interior ray did not find the corresponding face");
        Assert.Equal<StaticHandle?>(target, face.Body);
        // Known interior geometry only, not a production correspondence tolerance. The 0.1 mm checks are vertical,
        // where the highest fixture Y is 100 m. Horizontal float spacing at x 1024 is larger than that bound.
        Assert.InRange(heightError, 0f, 0.0001f);
        Assert.InRange(MathF.Abs(face.Normal.Y - expectedNormalY), 0f, 0.0001f);
        if (top is float topY)
        {
            Assert.InRange(MathF.Abs(hit.Point.Y - topY), 0f, 0.0001f);
            Assert.InRange(MathF.Abs(face.Point.Y - topY), 0f, 0.0001f);
        }
        if (shape is "slope" or "dome") Assert.True(face.Normal.Y < 0.9f);
        Assert.InRange(rise, float.Epsilon, 0.1f);
    }

    static (StaticHandle Body, float? Top, float NormalY) AddFaceProbeShape(BepuPhysicsWorld world,
        string shape, Vector3 offset)
    {
        if (shape is "mesh" or "mesh-interior")
        {
            var mesh = new TriangleMeshShape(
                [new(0f, RestingLip, -2f), new(2f, RestingLip, -2f),
                 new(0f, RestingLip, 2f), new(2f, RestingLip, 2f)], [0, 1, 2, 1, 3, 2]);
            return (world.AddStatic(mesh, Pose.At(offset)), offset.Y + RestingLip, 1f);
        }
        if (shape == "slope")
        {
            float angle = MathF.Acos(0.85f);
            var normal = new Vector3(-MathF.Sin(angle), MathF.Cos(angle), 0f);
            var pose = new Pose(offset + new Vector3(0f, 0.025f, Row) - normal * 0.025f,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle));
            return (world.AddStatic(new BoxShape(new Vector3(1f, 0.025f, 2f)), pose), null, 0.85f);
        }
        if (shape == "dome")
            return (world.AddStatic(new SphereShape(2f), Pose.At(offset + new Vector3(0f, -1.63f, Row))), null, 0.85f);
        if (shape == "wall")
            return (world.AddStatic(new BoxShape(new Vector3(1f, 1.5f, 2f)),
                Pose.At(offset + new Vector3(1f, 1.5f, 0f))), null, 0f);
        if (shape == "roof")
        {
            _ = world.AddStatic(new BoxShape(new Vector3(1f, RestingLip / 2f, 2f)),
                Pose.At(offset + new Vector3(1f, RestingLip / 2f, 0f)));
            return (world.AddStatic(new BoxShape(new Vector3(0.5f, 0.25f, 2f)),
                Pose.At(offset + new Vector3(-EdgeOffset, RoofBottom + 0.25f, 0f))), offset.Y + RoofBottom + 0.5f, 1f);
        }
        float lip = shape switch
        {
            "box" or "box-interior" => RestingLip,
            "low-box" => DeckTop,
            "high-box" => 0.3f,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        return (world.AddStatic(new BoxShape(new Vector3(1f, lip / 2f, 2f)),
            Pose.At(offset + new Vector3(1f, lip / 2f, 0f))), offset.Y + lip, 1f);
    }
}

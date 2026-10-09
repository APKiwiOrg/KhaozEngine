// Every expectation below is derived from the installed geometry, never from a stepper run. The footprint radius
// is half the default capsule radius, 0.2, and the band is the start feet plus or minus StepHeight 0.4.
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class GroundSeatTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly float FootRadius = 0.5f * Tuning.CapsuleRadius;
    static readonly float CosMaxSlope = MathF.Cos(Tuning.MaxSlopeRadians);
    static readonly Vector2 PlusX = Vector2.UnitX;

    static SupportSample Start(Func<float, float, float>? groundHeight, Func<float, float, Vector3>? groundNormal,
        IPhysicsWorld? world, IPhysicsQueryLease? lease, Vector2 axis, float feetY) =>
        FootSupport.Find(groundHeight, groundNormal, world, lease,
            new FootSupportQuery(axis, feetY, FootRadius, Tuning.StepHeight, Tuning.StepHeight, CosMaxSlope));

    /// <summary>Seats a move across a physics scene from <paramref name="startAxis"/> to <paramref name="axis"/>,
    /// taking the start support from the same scene.</summary>
    static GroundSeatResult Seat(FootSupportScene scene, Vector2 startAxis, float startFeetY, Vector2 axis,
        out SupportSample start)
    {
        start = Start(null, null, scene.World, scene.Lease, startAxis, startFeetY);
        return GroundSeat.Resolve(null, null, scene.World, scene.Lease, start, startAxis, startFeetY, axis,
            Vector2.Normalize(axis - startAxis), FootRadius, Tuning);
    }

    static void AssertOutcome(SeatOutcome expected, GroundSeatResult result) =>
        Assert.True(result.Outcome == expected, $"Expected {expected}, got {result}");

    static void AssertSeatedAt(double expected, GroundSeatResult result)
    {
        Assert.True(Math.Abs(result.FeetY - expected) <= result.Support.HeightError,
            $"Expected feet {expected:R}, got {result}");
    }

    static void AssertHorizontalUnit(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(0f, actual.Y);
        Assert.True(Vector3.Distance(expected, actual) <= 1e-6f, $"Expected {expected}, got {actual}");
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void FlatToLipSeatsWithTheWholeRiseAsStepPart(SceneVariant variant)
    {
        using FootSupportScene scene = Lip(variant);
        // The start disc reaches x -0.1, short of the lip at x 0. The new axis is 0.1 before the lip edge.
        GroundSeatResult result = Seat(scene, new Vector2(-0.3f, 0), 0, new Vector2(-0.1f, 0), out SupportSample start);

        AssertOutcome(SeatOutcome.Seated, result);
        Assert.Equal(scene["lip"], result.Support.Static);
        AssertSeatedAt(LipTop, result);
        // The level floor's plane explains none of the rise. Both heights carry their own error.
        Assert.True(Math.Abs(result.StepPart - LipTop) <= result.Support.HeightError + start.HeightError,
            $"StepPart {result.StepPart:R} in {result}");
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SlopeContinuationHasNoStepPart(SceneVariant variant)
    {
        using FootSupportScene scene = Slope(variant, 20f);
        float startFeetY = (float)scene.TopHeightAt("slope", 0.5f, 0);
        GroundSeatResult result = Seat(scene, new Vector2(0.5f, 0), startFeetY, new Vector2(0.6f, 0),
            out SupportSample start);

        AssertOutcome(SeatOutcome.Seated, result);
        AssertSeatedAt(scene.TopHeightAt("slope", 0.6f, 0), result);
        Assert.True(Math.Abs(result.StepPart) <= result.Support.HeightError + start.HeightError + 1e-6f,
            $"StepPart {result.StepPart:R} in {result}");
    }

    // The ramp is a 60 degree face rising from x 0. At the new axis 0.05 its plane is 0.087 above the start feet.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SteepRiseIsAWall(SceneVariant variant)
    {
        using FootSupportScene scene = Ramp(variant, 2f * MathF.Tan(Radians(60f)));
        GroundSeatResult result = Seat(scene, new Vector2(-0.15f, 0), 0, new Vector2(0.05f, 0), out _);

        AssertOutcome(SeatOutcome.Wall, result);
        AssertHorizontalUnit(-Vector3.UnitX, result.WallNormal);
        Assert.Equal(0f, result.FeetY);
    }

    // The ramp is a 60 degree face falling from the floor edge at x 0. At the new axis 0.21 the floor edge is
    // outside the disc and the face's plane is 0.364 below the start feet, inside the band.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SteepBelowSeatsSteep(SceneVariant variant)
    {
        using FootSupportScene scene = Ramp(variant, -2f * MathF.Tan(Radians(60f)));
        GroundSeatResult result = Seat(scene, new Vector2(0.01f, 0), 0, new Vector2(0.21f, 0), out _);

        AssertOutcome(SeatOutcome.SteepSeated, result);
        Assert.Equal(scene["ramp"], result.Support.Static);
        AssertSeatedAt(scene.TopHeightAt("ramp", 0.21f, 0), result);
    }

    // A ledge over x [-2, 0] above the floor at Y 0. The new axis 0.3 leaves the ledge edge outside the disc.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void LedgeBeyondStepHeightIsAirborne(SceneVariant variant)
    {
        static GroundSeatResult Drop(SceneVariant variant, float drop)
        {
            using FootSupportScene scene = new FootSupportScene(variant).Flat("floor", -4, 6, -5, 5, 0)
                .Flat("ledge", -2, 0, -2, 2, drop);
            return Seat(scene, new Vector2(-0.3f, 0), drop, new Vector2(0.3f, 0), out _);
        }

        GroundSeatResult falls = Drop(variant, 0.41f);
        AssertOutcome(SeatOutcome.Airborne, falls);
        Assert.Equal(0.41f, falls.FeetY);
        Assert.Equal(SupportStatus.None, falls.Support.Status);

        GroundSeatResult seats = Drop(variant, 0.40f);
        AssertOutcome(SeatOutcome.Seated, seats);
        AssertSeatedAt(0, seats);
    }

    [Fact]
    public void CurvedPropRefuses()
    {
        using FootSupportScene scene = new FootSupportScene(SceneVariant.Box).Flat("floor", -4, 6, -5, 5, 0);
        // A sphere of radius 0.25 centred on the floor at x 0.3. Its top, 0.25, is under the new axis.
        scene.World.AddStatic(new SphereShape(0.25f), Pose.At(new Vector3(0.3f, 0, 0)));
        GroundSeatResult result = Seat(scene, new Vector2(-0.2f, 0), 0, new Vector2(0.3f, 0), out SupportSample start);

        Assert.Equal(SupportStatus.Walkable, start.Status);
        AssertOutcome(SeatOutcome.Refused, result);
        Assert.Equal(0f, result.FeetY);
    }

    // Flat until x 0.1, then rising 5 in 1, so the terrain is 0.5 at x 0.2, above the start feet plus StepHeight.
    // The diagonal move separates the terrain normal from the reverse of the move.
    [Fact]
    public void AnalyticCliffIsAWall()
    {
        static float Height(float x, float z) => MathF.Max(0, 5 * (x - 0.1f));
        static Vector3 Normal(float x, float z) =>
            x > 0.1f ? Vector3.Normalize(new Vector3(-5, 1, 0)) : Vector3.UnitY;
        Vector2 startAxis = Vector2.Zero, axis = new(0.2f, 0.2f);
        Vector2 direction = Vector2.Normalize(axis - startAxis);

        SupportSample start = Start(Height, Normal, null, null, startAxis, 0);
        GroundSeatResult withNormal = GroundSeat.Resolve(Height, Normal, null, null, start, startAxis, 0, axis,
            direction, FootRadius, Tuning);
        AssertOutcome(SeatOutcome.Wall, withNormal);
        AssertHorizontalUnit(-Vector3.UnitX, withNormal.WallNormal);
        Assert.Equal(0f, withNormal.FeetY);

        SupportSample bare = Start(Height, null, null, null, startAxis, 0);
        GroundSeatResult withoutNormal = GroundSeat.Resolve(Height, null, null, null, bare, startAxis, 0, axis,
            direction, FootRadius, Tuning);
        AssertOutcome(SeatOutcome.Wall, withoutNormal);
        AssertHorizontalUnit(new Vector3(-direction.X, 0, -direction.Y), withoutNormal.WallNormal);
    }
}

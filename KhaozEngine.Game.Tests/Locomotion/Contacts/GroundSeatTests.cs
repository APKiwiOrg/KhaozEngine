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
            Vector2.Normalize(axis - startAxis), FootRadius, Tuning, CosMaxSlope);
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

    // The witness lies on the support under the sample's axis, as for a face under the axis.
    static SupportSample Sample(float height, Vector3 normal, float error = 0, float axisX = 0) =>
        new(SupportStatus.Walkable, height, error, normal, null, -1, new Vector3(axisX, height, 0));

    [Fact]
    public void RiserBetweenLevelTreadsIsItsHeight()
    {
        SupportSample start = Sample(0, Vector3.UnitY), next = Sample(0.18f, Vector3.UnitY);
        Assert.Equal(0.18f, GroundSeat.DiscreteStep(start, Vector2.Zero, next, new Vector2(0.1f, 0)));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void ConvexAndConcaveCurvesAreNotSteps(float sign)
    {
        const float x0 = 0.3f, x1 = 0.4f;
        SupportSample start = Sample(sign * 0.5f * x0 * x0, Vector3.Normalize(new Vector3(-sign * x0, 1, 0)),
            axisX: x0);
        SupportSample next = Sample(sign * 0.5f * x1 * x1, Vector3.Normalize(new Vector3(-sign * x1, 1, 0)),
            axisX: x1);
        Assert.Equal(0f, GroundSeat.DiscreteStep(start, new Vector2(x0, 0), next, new Vector2(x1, 0)));
    }

    [Fact]
    public void PlanarRampIsExactlyZero()
    {
        float angle = Radians(30);
        Vector3 normal = new(-MathF.Sin(angle), MathF.Cos(angle), 0);
        // The second point is one unit along the tangent, so its float coordinates also define the plane.
        SupportSample start = Sample(0, normal), next = Sample(-normal.X, normal, axisX: normal.Y);
        Assert.Equal(0f, GroundSeat.DiscreteStep(start, Vector2.Zero, next, new Vector2(normal.Y, 0)));
    }

    [Fact]
    public void DropIsTheNegativeHeight()
    {
        SupportSample start = Sample(0.18f, Vector3.UnitY), next = Sample(0, Vector3.UnitY);
        Assert.Equal(-0.18f, GroundSeat.DiscreteStep(start, Vector2.Zero, next, new Vector2(0.1f, 0)));
    }

    [Fact]
    public void StepOntoASlopedTreadIsTheRiser()
    {
        float grade = MathF.Tan(Radians(10));
        SupportSample start = Sample(0, Vector3.UnitY);
        SupportSample next = Sample(0.18f, Vector3.Normalize(new Vector3(-grade, 1, 0)), axisX: 0.1f);
        float step = GroundSeat.DiscreteStep(start, Vector2.Zero, next, new Vector2(0.1f, 0));
        // rf is 0.18. rb is 0.18 less the new tread's rise over the 0.1 move.
        Assert.True(step > 0 && Math.Abs(step - 0.18f) <= 0.1 * grade, $"Discrete step {step:R}");
    }

    // Past a sloped face's top edge the footprint overhangs the edge. The certified height is the edge's, below the
    // face's plane at the axis, so the height field is level there and walking on is not a step. The edge is at
    // x 2 on a face rising +X, and the body walks from x 1.95 on the face to x 2.05 past it.
    [Theory]
    [InlineData(14f)]
    [InlineData(30f)]
    public void OverhungSlopeEdgeIsNotAStep(float degrees)
    {
        float grade = MathF.Tan(Radians(degrees)), top = 2 * grade;
        Vector3 face = Vector3.Normalize(new Vector3(-grade, 1, 0));
        SupportSample start = Sample(top - 0.05f * grade, face, axisX: 1.95f);
        SupportSample next = Sample(top, face) with { Witness = new Vector3(2, top, 0) };
        Assert.Equal(0f, GroundSeat.DiscreteStep(start, new Vector2(1.95f, 0), next, new Vector2(2.05f, 0)));
        Assert.Equal(0f, GroundSeat.DiscreteStep(next, new Vector2(2.05f, 0), next, new Vector2(2.15f, 0)));
    }

    [Theory]
    [InlineData((int)SupportStatus.Steep, false)]
    [InlineData((int)SupportStatus.Steep, true)]
    [InlineData((int)SupportStatus.None, false)]
    [InlineData((int)SupportStatus.None, true)]
    [InlineData((int)SupportStatus.Refused, false)]
    [InlineData((int)SupportStatus.Refused, true)]
    public void NonWalkableEitherSideIsZero(int status, bool atStart)
    {
        SupportSample start = Sample(0, Vector3.UnitY), next = Sample(0.18f, Vector3.UnitY);
        if (atStart) start = start with { Status = (SupportStatus)status };
        else next = next with { Status = (SupportStatus)status };
        Assert.Equal(0f, GroundSeat.DiscreteStep(start, Vector2.Zero, next, new Vector2(0.1f, 0)));
    }

    [Theory]
    [InlineData(0.18f)]
    [InlineData(-0.18f)]
    public void CertifiedHeightErrorAbsorbsTheResidual(float height)
    {
        SupportSample start = Sample(0, Vector3.UnitY, MathF.Abs(height) / 2);
        SupportSample next = Sample(height, Vector3.UnitY, MathF.Abs(height) / 2);
        Assert.Equal(0f, GroundSeat.DiscreteStep(start, Vector2.Zero, next, new Vector2(0.1f, 0)));
    }

    [Theory]
    [InlineData(1f, false, false)]
    [InlineData(-1f, false, false)]
    [InlineData(1f, true, false)]
    [InlineData(-1f, true, false)]
    [InlineData(1f, false, true)]
    [InlineData(-1f, false, true)]
    [InlineData(1f, true, true)]
    [InlineData(-1f, true, true)]
    public void ContactSkinAndErrorsBoundTheSmallestStep(float sign, bool withErrors, bool above)
    {
        float error = withErrors ? ShellMotion.ContactSkin / 2 : 0;
        float height = withErrors ? 2 * ShellMotion.ContactSkin : ShellMotion.ContactSkin;
        if (above) height = MathF.BitIncrement(height);
        height *= sign;
        SupportSample start = Sample(0, Vector3.UnitY, error), next = Sample(height, Vector3.UnitY, error);

        Assert.Equal(above ? height : 0f,
            GroundSeat.DiscreteStep(start, Vector2.Zero, next, new Vector2(0.1f, 0)));
    }

    // The step part is charged against the climb budget, so it rounds up, never to nearest. Feet at -2^-28 seated
    // on terrain at 0.25 + 2^-25 rise 0.25 + 2^-25 + 2^-28, which lies between floats and rounds to nearest below
    // itself. Both the level reference and a start plane read the same rise.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StepPartNeverUnderstatesTheRise(bool startPlane)
    {
        float startFeetY = -MathF.ScaleB(1f, -28);
        float height = 0.25f + MathF.ScaleB(1f, -25);
        double rise = (double)height - startFeetY;
        Assert.True((double)(float)rise < rise, $"The rise {rise:R} must round to nearest below itself.");
        SupportSample start = startPlane
            ? new(SupportStatus.Walkable, startFeetY, 0, Vector3.UnitY, null, -1, new Vector3(0, startFeetY, 0))
            : new(SupportStatus.None, float.NaN, float.NaN, Vector3.Zero, null, -1, Vector3.Zero);

        GroundSeatResult result = GroundSeat.Resolve((_, _) => height, null, null, null, start, Vector2.Zero,
            startFeetY, new Vector2(0.1f, 0), PlusX, FootRadius, Tuning, CosMaxSlope);

        AssertOutcome(SeatOutcome.Seated, result);
        Assert.True(result.StepPart >= rise, $"StepPart {result.StepPart:R} below the rise {rise:R}");
        Assert.Equal(MathF.BitIncrement((float)rise), result.StepPart);
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

    // A sphere of radius 0.25 centred on the floor at x 0.3. Its top, 0.25, is under the new axis, within the band
    // and level there, so the curved prop certifies and seats the body on its top.
    [Fact]
    public void CurvedPropSeatsOnItsTop()
    {
        using FootSupportScene scene = new FootSupportScene(SceneVariant.Box).Flat("floor", -4, 6, -5, 5, 0);
        StaticHandle sphere = scene.World.AddStatic(new SphereShape(0.25f), Pose.At(new Vector3(0.3f, 0, 0)));
        GroundSeatResult result = Seat(scene, new Vector2(-0.2f, 0), 0, new Vector2(0.3f, 0), out SupportSample start);

        Assert.Equal(SupportStatus.Walkable, start.Status);
        AssertOutcome(SeatOutcome.Seated, result);
        Assert.Equal(sphere, result.Support.Static);
        AssertSeatedAt(0.25, result);
    }

    // An analytic cliff ahead takes the move's reverse as its wall normal when the terrain gives none, so a zero or
    // non-finite move direction is rejected before any rule runs.
    [Fact]
    public void ZeroMoveDirectionThrows()
    {
        static float Height(float x, float z) => x > 0.1f ? 1 : 0;
        SupportSample start = Start(Height, null, null, null, Vector2.Zero, 0);
        Vector2[] directions = [Vector2.Zero, new(float.NaN, 0), new(0, float.PositiveInfinity)];
        foreach (Vector2 direction in directions)
            Assert.Throws<ArgumentOutOfRangeException>(() => GroundSeat.Resolve(Height, null, null, null, start,
                Vector2.Zero, 0, new Vector2(0.2f, 0), direction, FootRadius, Tuning, CosMaxSlope));
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
            direction, FootRadius, Tuning, CosMaxSlope);
        AssertOutcome(SeatOutcome.Wall, withNormal);
        AssertHorizontalUnit(-Vector3.UnitX, withNormal.WallNormal);
        Assert.Equal(0f, withNormal.FeetY);

        SupportSample bare = Start(Height, null, null, null, startAxis, 0);
        GroundSeatResult withoutNormal = GroundSeat.Resolve(Height, null, null, null, bare, startAxis, 0, axis,
            direction, FootRadius, Tuning, CosMaxSlope);
        AssertOutcome(SeatOutcome.Wall, withoutNormal);
        AssertHorizontalUnit(new Vector3(-direction.X, 0, -direction.Y), withoutNormal.WallNormal);
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Headless movement over the #1429 pitched plank and the #1455 lying logs (CF-6). Each row walks one bounded
/// input sequence through the dry tick's own interval (its lease and open memo) and must reach the target within the
/// 1 mm closed arrival ball, on the height the independent oracle derives for an identical installation. A 0.3 m log's
/// 0.6 m crest is above the 0.4 m step height, so its approach from the floor is blocked (ruling FA-R8). A body seated on
/// that crest walks along it or down its walkable flank instead.</summary>
public sealed class ContactFollowupTraversalTests(ITestOutputHelper output)
{
    internal static readonly MoveTuning Tuning = MoveTuning.Default;
    internal static readonly GroundCoreSettings Settings = new(0.5f);
    internal const float Dt = 1f / 60f;
    internal const float Speed = 1f;
    internal const int MaxTicks = 240;
    internal const float Approach = 0.75f;

    internal static float FootRadius => Settings.FootRadiusFraction * Tuning.CapsuleRadius;

    internal static float CosMaxSlope => MathF.Cos(Tuning.MaxSlopeRadians);

    public static IEnumerable<object[]> PlankRows()
    {
        foreach (float pitch in new[] { 0.05235988f, 0.10471976f })
            foreach (bool compound in new[] { false, true })
                foreach (int sign in new[] { -1, 1 })
                    foreach (bool stopOnProp in new[] { false, true })
                        yield return [pitch, compound, sign, stopOnProp];
    }

    public static IEnumerable<object[]> LogRows()
    {
        foreach (bool capsule in new[] { false, true })
            foreach (float radius in new[] { 0.1f, 0.15f })
                foreach (bool aboutX in new[] { false, true })
                    foreach (int sign in new[] { -1, 1 })
                        foreach (bool stopOnProp in new[] { false, true })
                            yield return [capsule, radius, aboutX, sign, stopOnProp];
    }

    public static IEnumerable<object[]> TallLogRows()
    {
        foreach (bool capsule in new[] { false, true })
            foreach (bool aboutX in new[] { false, true })
                foreach (int sign in new[] { -1, 1 })
                    yield return [capsule, aboutX, sign];
    }

    public static IEnumerable<object[]> TallLogCrestRows()
    {
        foreach (bool capsule in new[] { false, true })
            foreach (bool aboutX in new[] { false, true })
                foreach (bool along in new[] { false, true })
                    yield return [capsule, aboutX, along];
    }

    [Theory]
    [MemberData(nameof(PlankRows))]
    public void PlankTraversalArrivesWithinOneMillimetre(float pitch, bool compound, int sign, bool stopOnProp)
    {
        var (scene, obstacle, direction) = ContactFollowupScenes.Plate(pitch, compound);
        using (scene)
        {
            Vector2 along = sign * direction;
            Vector2 start = ContactFollowupScenes.PlateCentre - Approach * along;
            Vector2 target = stopOnProp ? ContactFollowupScenes.PlateCentre : ContactFollowupScenes.PlateCentre + Approach * along;
            Traverse(scene, obstacle, compound ? ContactFollowupScenes.CompoundLeaf : 0, start, 0, target, plank: true,
                query => ContactFollowupScenes.PlateSupport(pitch, compound, query));
        }
    }

    [Theory]
    [MemberData(nameof(LogRows))]
    public void LogTraversalArrivesWithinOneMillimetre(bool capsule, float radius, bool aboutX, int sign, bool stopOnProp)
    {
        var (scene, obstacle, direction) = ContactFollowupScenes.Log(capsule, radius, aboutX);
        using (scene)
        {
            Vector2 along = sign * direction;
            Vector2 start = -Approach * along;
            Vector2 target = stopOnProp ? Vector2.Zero : Approach * along;
            Traverse(scene, obstacle, 0, start, 0, target, plank: false,
                query => ContactFollowupScenes.LogSupport(capsule, radius, aboutX, query));
        }
    }

    // The lifted shell's lower sphere, centre StepHeight + CapsuleRadius above the feet, meets the 0.3 m log where the
    // centres are 0.7 apart: |across| = sqrt(0.7^2 - (0.8 - 0.3)^2). The skin keeps the stop just short of it.
    [Theory]
    [MemberData(nameof(TallLogRows))]
    public void TallLogApproachIsBlockedBelowItsCrest(bool capsule, bool aboutX, int sign)
    {
        const float radius = 0.3f;
        var (scene, obstacle, direction) = ContactFollowupScenes.Log(capsule, radius, aboutX);
        using (scene)
        {
            Vector2 along = sign * direction;
            Vector2 target = Approach * along;
            var start = new Vector3(-Approach * along.X, 0, -Approach * along.Y);
            double contact = Math.Sqrt(0.7 * 0.7 - (double)(Tuning.StepHeight + Tuning.CapsuleRadius - radius) *
                (Tuning.StepHeight + Tuning.CapsuleRadius - radius));
            var inputs = new List<Vector2>();
            var results = new List<GroundStepResult>();
            Vector3 feet = start;
            bool blocked = false;
            for (int tick = 0; tick < 60; tick++)
            {
                Vector2 move = Command(feet, target);
                GroundStepResult result = DryTick(scene.World, feet, move, false);
                inputs.Add(move);
                results.Add(result);
                Assert.Equal(GroundFooting.Walkable, result.Footing);
                Assert.Equal(0f, result.Feet.Y);
                Assert.Equal(0f, result.StepRise);
                Assert.False(OnObstacle(result.Support, obstacle, 0));
                Assert.False(scene.World.ComputePenetration(ShellGeometry.Shape(Tuning),
                    Pose.At(ShellGeometry.Centre(result.Feet, Tuning)), out _), $"The shell overlaps at {result}");
                // Once the shell meets the log, every later tick stays blocked at that contact.
                double reached = Vector2.Dot(new Vector2(result.Feet.X, result.Feet.Z), -along);
                blocked |= result.Blocked;
                if (blocked)
                {
                    Assert.True(result.Blocked);
                    Assert.InRange(reached, contact + ShellMotion.ContactSkin / 2, contact + 2 * ShellMotion.ContactSkin);
                }
                feet = result.Feet;
            }
            output.WriteLine($"stopped at {results[^1].Feet}, contact {contact:R} from the log axis");
            Assert.True(blocked);

            feet = start;
            for (int i = 0; i < inputs.Count; i++)
            {
                GroundStepResult replay = DryTick(scene.World, feet, inputs[i], false);
                AssertBits(results[i], replay, $"tick {i + 1}");
                feet = replay.Feet;
            }
        }
    }

    // Seated on the 0.3 m crest, the body walks 0.25 m along the crest, clear of the end seams, or 0.3 m across it and down
    // the walkable flank, short of the steep part that starts near 0.42 m. The fall past that point is not part of this proof.
    [Theory]
    [MemberData(nameof(TallLogCrestRows))]
    public void TallLogCrestWalkArrivesWithinOneMillimetre(bool capsule, bool aboutX, bool along)
    {
        const float radius = 0.3f;
        var (scene, obstacle, direction) = ContactFollowupScenes.Log(capsule, radius, aboutX);
        using (scene)
        {
            FootSupportQuery crest = new(Vector2.Zero, 2 * radius, FootRadius, Tuning.StepHeight, Tuning.StepHeight,
                CosMaxSlope);
            SupportSample seat = Find(scene, crest);
            FootprintOracleSupport expected = ContactFollowupScenes.LogSupport(capsule, radius, aboutX, crest);
            Assert.Equal(SupportStatus.Walkable, seat.Status);
            Assert.True(OnObstacle(seat, obstacle, 0));
            Assert.True((double)seat.Height - seat.HeightError <= expected.HeightLower);
            Assert.True((double)seat.Height + seat.HeightError >= expected.HeightUpper);
            Vector2 axis = aboutX ? Vector2.UnitY : Vector2.UnitX;
            Vector2 target = along ? 0.25f * axis : radius * direction;
            Traverse(scene, obstacle, 0, Vector2.Zero, seat.Height, target, plank: false,
                query => ContactFollowupScenes.LogSupport(capsule, radius, aboutX, query));
        }
    }

    static SupportSample Find(FootSupportScene scene, in FootSupportQuery query)
    {
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        return FootSupport.Find(null, scene.World, lease, query);
    }

    void Traverse(FootSupportScene scene, StaticHandle obstacle, int obstacleFeatures, Vector2 start, float startY,
        Vector2 target, bool plank, Func<FootSupportQuery, FootprintOracleSupport> oracle)
    {
        var inputs = new List<(Vector2 Move, bool Stepping)>();
        var results = new List<GroundStepResult>();
        var feet = new Vector3(start.X, startY, start.Y);
        bool stepping = false, sawObstacleSupport = false;
        GroundStepResult result = default;
        int ticks = 0;
        while (ticks < MaxTicks)
        {
            Vector2 move = Command(feet, target);
            GroundStepResult previous = result;
            result = DryTick(scene.World, feet, move, stepping);
            inputs.Add((move, stepping));
            results.Add(result);
            ticks++;
            output.WriteLine($"tick {ticks}: {feet} -> {result.Feet} {result.Footing} blocked {result.Blocked}, " +
                $"support {result.Support.Static?.Value}/{result.Support.FeatureId} {result.Support.Height:R} " +
                $"+-{result.Support.HeightError:R}, step rise {result.StepRise:R}");
            Assert.Equal(GroundFooting.Walkable, result.Footing);
            bool onObstacle = OnObstacle(result.Support, obstacle, obstacleFeatures);
            sawObstacleSupport |= onObstacle;
            // A plank tick that starts and ends seated on the same continuous top face pays no crossing climb.
            if (plank && onObstacle && ticks > 1 && previous.Support.Static == result.Support.Static &&
                previous.Support.FeatureId == result.Support.FeatureId && Seated(previous))
                Assert.Equal(0f, result.StepRise);
            feet = result.Feet;
            stepping = result.StepRise > 0;
            if (Vector2.Distance(new(feet.X, feet.Z), target) <= 0.001f && Seated(result)) break;
        }

        FootprintOracleSupport expected = oracle(new FootSupportQuery(target, result.Feet.Y, FootRadius,
            Tuning.StepHeight, Tuning.StepHeight, CosMaxSlope));
        var expectedTarget = new Vector3(target.X, (float)expected.Height, target.Y);
        output.WriteLine($"arrived after {ticks} ticks at {result.Feet}, oracle {expected.Status} " +
            $"{expected.Static?.Value}/{expected.ElementId} {expected.Height:R}");
        Assert.Equal(GroundFooting.Walkable, result.Footing);
        Assert.False(result.Blocked);
        Assert.InRange(result.Support.HeightError, 0f, 0.0005f);
        Assert.True(ticks <= MaxTicks);
        Assert.True(sawObstacleSupport);
        Assert.Equal(SupportStatus.Walkable, expected.Status);
        Assert.Equal(expected.Static, result.Support.Static);
        Assert.Equal(expected.ElementId, result.Support.FeatureId);
        Assert.True((double)result.Support.Height - result.Support.HeightError <= expected.HeightLower);
        Assert.True((double)result.Support.Height + result.Support.HeightError >= expected.HeightUpper);
        // No undershoot once the debt is paid and no placement lift above the support in the dry head.
        Assert.InRange(Math.Abs((double)result.Feet.Y - result.Support.Height), 0, result.Support.HeightError);
        Assert.True(Vector2.Distance(new(result.Feet.X, result.Feet.Z), target) <= 0.001f);
        Assert.True(Vector3.Distance(result.Feet, expectedTarget) <= 0.001f);

        // The same recorded inputs from the copied initial state reproduce every geometric field bit for bit. Each
        // replay tick acquires its own lease, so its evidence authenticates to its own interval.
        feet = new Vector3(start.X, startY, start.Y);
        for (int i = 0; i < inputs.Count; i++)
        {
            GroundStepResult replay = DryTick(scene.World, feet, inputs[i].Move, inputs[i].Stepping);
            AssertBits(results[i], replay, $"tick {i + 1}");
            feet = replay.Feet;
        }
    }

    /// <summary>One tick's horizontal command: <c>min(remaining, Speed * Dt)</c> toward the target, zero once there.</summary>
    internal static Vector2 Command(Vector3 feet, Vector2 target)
    {
        Vector2 remaining = target - new Vector2(feet.X, feet.Z);
        float length = remaining.Length();
        return length > 0 ? remaining * (MathF.Min(length, Speed * Dt) / length) : Vector2.Zero;
    }

    /// <summary>True when the sample is the obstacle's own geometry: its static, and past the floor leaf in a
    /// compound.</summary>
    internal static bool OnObstacle(in SupportSample support, StaticHandle obstacle, int obstacleFeatures) =>
        support.Status == SupportStatus.Walkable && support.Static == obstacle && support.FeatureId >= obstacleFeatures;

    /// <summary>Feet within the certified error of their walkable support, so no climb is owed.</summary>
    internal static bool Seated(in GroundStepResult result) =>
        result.Support.Status == SupportStatus.Walkable &&
        Math.Abs((double)result.Feet.Y - result.Support.Height) <= result.Support.HeightError;

    // The dry tick's own interval, as ContactController.Step opens it: one read lease and the thread's dry memo.
    static GroundStepResult DryTick(BepuPhysicsWorld world, Vector3 feet, Vector2 move, bool stepping)
    {
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        SupportRequestMemo? memo = SupportRequestMemo.OpenDry(lease);
        Assert.NotNull(memo);
        try
        {
            var queries = new DryContactQueryContext(null, null, world, lease, Tuning, null, memo);
            GroundStepResult result = GroundCore.Step(feet, move, Dt, Tuning, Settings, queries, stepInProgress: stepping);
            if (result.Support.Static is not null && result.Support.Status == SupportStatus.Walkable)
                Assert.Same(lease, result.Support.Source.Result.Lease);
            return result;
        }
        finally { memo!.Close(); }
    }

    /// <summary>Bitwise equality over every geometric field. CF-4 interval evidence is excluded.</summary>
    internal static void AssertBits(object expected, object actual, string path)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        if (expected is float f)
        {
            Assert.True(BitConverter.SingleToInt32Bits(f) == BitConverter.SingleToInt32Bits((float)actual),
                $"{path}: expected {f:R}, actual {actual}");
            return;
        }
        if (expected is double d)
        {
            Assert.True(BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits((double)actual),
                $"{path}: expected {d:R}, actual {actual}");
            return;
        }
        Type type = expected.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(string)) { Assert.Equal(expected, actual); return; }
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType == typeof(SupportContinuityHandle)) continue;
            object? left = field.GetValue(expected), right = field.GetValue(actual);
            if (left is null) Assert.Null(right);
            else { Assert.NotNull(right); AssertBits(left, right!, path + "." + field.Name); }
        }
    }
}

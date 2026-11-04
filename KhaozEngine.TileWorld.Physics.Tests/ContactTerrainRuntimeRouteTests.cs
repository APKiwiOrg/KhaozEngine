using System.Numerics;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainRuntimeRouteTests
{
    // #1697. A predicted Hold must not let the wrapper replace the command before it reaches the real body.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalBodyExecutesItsHoldBeforeTheDropIsAcknowledged(bool point)
    {
        using var scene = new ContactTerrainScene(4);
        var nav = ContactTerrainRuntimeTests.Load(scene);
        var tuning = ContactTerrainScene.Tuning;
        var driver = new ContactTerrainCommandDriver(nav, point);
        var body = new MoveState
        {
            Position = scene.From + Vector3.UnitY * tuning.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1
        };
        Vector3 goal = scene.To + new Vector3(0.5f, 0, 0);
        for (int tick = 0; tick < 120; tick++)
        {
            var landing = driver.ActivePath?.Waypoints.FirstOrDefault(waypoint => waypoint.Kind == KhaozEngine.Navigation.NavWaypointKind.Drop);
            bool atLanding = false;
            if (landing is { Kind: KhaozEngine.Navigation.NavWaypointKind.Drop } marker)
            {
                var grid = nav.Space.Layers[marker.Layer];
                var cell = grid.CellOf(marker.Position.X, marker.Position.Y);
                atLanding = body.Grounded && GroundTraversalProbe.Near(body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight,
                    new(marker.Position.X, grid.RestingHeightAt(cell.X, cell.Z)!.Value, marker.Position.Y));
            }
            var input = new PlayerPathInput(0.7f + tick * 0.1f);
            var steering = driver.Tick(body, tuning, goal, false, scene.Context, input);
            if (atLanding)
            {
                Assert.True(driver.HasPendingDrop);
                Assert.Equal(RangeMoveStatus.Dropping, steering.Status);
                Assert.Equal(Vector2.Zero, steering.WorldDirection);
                body = ContactTerrainRuntimeTests.Step(scene.Context, body, input.Command(steering, false));
                _ = driver.Tick(body, tuning, goal, false, scene.Context, new PlayerPathInput(input.CameraYaw + 0.1f));
                Assert.False(driver.HasPendingDrop);
                return;
            }
            body = ContactTerrainRuntimeTests.Step(scene.Context, body, input.Command(steering, false));
        }
        Assert.Fail("The actual body did not exercise the terminal Hold boundary.");
    }

    [Theory]
    [InlineData(false, "alignment")]
    [InlineData(true, "alignment")]
    [InlineData(false, "prefix")]
    [InlineData(true, "prefix")]
    [InlineData(false, "rooted")]
    [InlineData(true, "rooted")]
    public void ChangedGoalReleasesSupportedPreparationBeforeDeparture(bool point, string phase)
    {
        using var scene = new ContactTerrainScene(4);
        var nav = ContactTerrainRuntimeTests.Load(scene);
        var tuning = ContactTerrainScene.Tuning;
        // A broad Navigation radius exposes reversible physical alignment before the 1 mm departure gate.
        var driver = new ContactTerrainCommandDriver(nav, point, acceptRadius: 0.1f);
        var body = new MoveState
        {
            Position = scene.From + new Vector3(-0.08f, tuning.CapsuleHalfHeight, 0.03f),
            Grounded = true,
            SpeedScale = phase == "rooted" ? 0 : 0.25f
        };
        bool lastDropping = false;
        for (int tick = 0; tick < 120; tick++)
        {
            var input = new PlayerPathInput(0.7f + tick * 0.1f);
            var feet = body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight;
            bool replace = phase == "prefix" ? lastDropping && feet.X > scene.From.X : tick == 1;
            var goal = replace ? scene.From - new Vector3(0.25f, 0, 0) : scene.To;
            var steering = driver.Tick(body, tuning, goal, false, scene.Context, input);
            Assert.True(body.Grounded);
            if (replace)
            {
                Assert.NotEqual(RangeMoveStatus.Dropping, steering.Status);
                Assert.True(steering.WorldDirection.X <= 0);
                return;
            }
            body = ContactTerrainRuntimeTests.Step(scene.Context, body, input.Command(steering, false));
            lastDropping = steering.Status == RangeMoveStatus.Dropping;
        }
        Assert.Fail("The supported launch prefix was not exercised.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGoalContainingTheLowerFringeCannotConsumeTheDrop(bool point)
    {
        using var scene = new ContactTerrainScene(4);
        var nav = ContactTerrainRuntimeTests.Load(scene);
        var tuning = ContactTerrainScene.Tuning;
        var driver = new ContactTerrainCommandDriver(nav, point);
        var body = new MoveState
        {
            Position = scene.From + Vector3.UnitY * tuning.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1
        };
        bool descended = false, changed = false;
        var goal = scene.To;
        for (int tick = 0; tick < 120; tick++)
        {
            var feet = body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight;
            bool fringe = descended && body.Grounded && !nav.Footprint.Accepts(feet);
            if (fringe) { goal = feet; changed = true; }
            var input = new PlayerPathInput(0.7f + tick * 0.1f);
            var steering = driver.Tick(body, tuning, goal, false, scene.Context, input);
            if (fringe) Assert.Equal(RangeMoveStatus.Dropping, steering.Status);
            if (changed && body.Grounded && GroundTraversalProbe.Near(feet, scene.To) && steering.Status != RangeMoveStatus.Dropping)
            {
                Assert.NotEqual(RangeMoveStatus.Blocked, steering.Status);
                if (point) Assert.Equal(1, driver.CompletedDrops);
                return;
            }
            body = ContactTerrainRuntimeTests.Step(scene.Context, body, input.Command(steering, false));
            descended |= !body.Grounded;
        }
        Assert.Fail("The committed Drop did not reach its real terminal after goal replacement.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConsecutiveDropsCompleteSeparatelyBeforeTheFinalWalk(bool point, bool run)
    {
        var document = ContactTerrainScene.Document(8, profile: x => x <= 1 ? 8 : x < 2 ? 4 * (3 - x)
            : x <= 3 ? 4 : x < 4 ? 4 * (4 - x) : 0);
        using var scene = new ContactTerrainScene(8, document: document);
        var nav = ContactTerrainRuntimeTests.Load(scene);
        var tuning = ContactTerrainScene.Tuning;
        var driver = new ContactTerrainCommandDriver(nav, point);
        var body = new MoveState
        {
            Position = scene.From + Vector3.UnitY * tuning.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1
        };
        var goal = new Vector3(4.625f, 0, scene.To.Z);
        int departures = 0;
        for (int tick = 0; tick < 240; tick++)
        {
            var input = new PlayerPathInput(0.7f + tick * 0.1f);
            var steering = driver.Tick(body, tuning, goal, run, scene.Context, input);
            Assert.Contains(steering.Status, new[] { RangeMoveStatus.Following, RangeMoveStatus.Dropping, RangeMoveStatus.InRange });
            if (steering.Status == RangeMoveStatus.InRange)
            {
                Assert.Equal(2, departures);
                if (point) Assert.Equal(2, driver.CompletedDrops);
                Assert.True(body.Grounded);
                return;
            }
            var next = ContactTerrainRuntimeTests.Step(scene.Context, body, input.Command(steering, run));
            if (body.Grounded && !next.Grounded) departures++;
            body = next;
        }
        Assert.Fail("The consecutive route did not finish.");
    }
}

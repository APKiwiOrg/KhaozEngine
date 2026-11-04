using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainRuntimeTests
{
    // #1697. Stored terrain routes must run through real camera-relative commands, including the lower fringe.
    [Theory]
    [InlineData(1.3f, false, false)]
    [InlineData(1.3f, false, true)]
    [InlineData(1.3f, true, false)]
    [InlineData(1.3f, true, true)]
    [InlineData(4f, false, false)]
    [InlineData(4f, false, true)]
    [InlineData(4f, true, false)]
    [InlineData(4f, true, true)]
    [InlineData(6f, false, false)]
    [InlineData(6f, false, true)]
    [InlineData(6f, true, false)]
    [InlineData(6f, true, true)]
    public void LoadedTerrainRouteReachesExactPointThroughTheActualBody(float height, bool run, bool point)
    {
        using var scene = new ContactTerrainScene(height);
        var navigation = Load(scene);
        var move = new ContactTerrainCommandDriver(navigation, point);
        var tuning = ContactTerrainScene.Tuning;
        var body = new MoveState
        {
            Position = scene.From + new Vector3(-0.08f, tuning.CapsuleHalfHeight, 0.03f),
            Grounded = true,
            SpeedScale = 1,
        };
        Assert.True(navigation.Footprint.Accepts(body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight));
        var target = ReachTarget.Point(scene.To + new Vector3(0.25f, tuning.CapsuleHalfHeight, 0));
        bool descending = false, fringe = false;
        var statuses = new List<RangeMoveStatus>();
        for (int tick = 0; tick < 240; tick++)
        {
            var input = new PlayerPathInput(0.7f + tick * 0.1f);
            var steering = move.Tick(body, tuning, scene.To + new Vector3(0.25f, 0, 0), run, scene.Context, input);
            statuses.Add(steering.Status);
            Assert.DoesNotContain(steering.Status, new[] { RangeMoveStatus.Blocked, RangeMoveStatus.Suspended, RangeMoveStatus.Unreachable });
            Vector3 feet = body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight;
            if (descending && body.Grounded && !navigation.Footprint.Accepts(feet))
            {
                fringe = true;
                Assert.Equal(RangeMoveStatus.Dropping, steering.Status);
            }
            if (steering.Status == RangeMoveStatus.InRange) break;
            body = Step(scene.Context, body, input.Command(steering, run));
            descending |= !body.Grounded;
        }
        Assert.True(descending);
        Assert.True(fringe);
        Assert.Contains(RangeMoveStatus.Dropping, statuses);
        Assert.Equal(RangeMoveStatus.InRange, statuses[^1]);
        if (point) Assert.True(move.CompletedDrops > 0);
        Assert.True(body.Grounded);
        Assert.True(navigation.Footprint.Accepts(body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight));
        Assert.True(ReachGeometry.Within(new(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight), target, 0));
    }

    internal static GroundNavigation Load(ContactTerrainScene scene)
    {
        var options = ContactTerrainOptInTests.Options with { CaptureTerrainDescentEvidence = true };
        var sources = new NavBakeSources().Add("world", new byte[32]);
        var profiles = new[] { new NavBakeProfile("player", ContactTerrainScene.Tuning, default)
            { MaxDropMetres = 6, AllowTerrainDescents = true } };
        using var capture = PhysicsNavBake.Capture(scene.Context, options, _ => 0);
        var bake = GroundNavigationBake.Create(capture, sources, profiles);
        using var stream = new MemoryStream();
        bake.WriteTo(stream);
        stream.Position = 0;
        var loaded = GroundNavigationBake.Load(stream, new(options, sources, profiles));
        Assert.Equal(NavBakeLoadStatus.Loaded, loaded.Status);
        var navigation = loaded.Bake!.GetProfile("player");
        Assert.Contains(navigation.Graph.Links, link => navigation.Terrain!.Route(link) == ContactDropRoute.Terrain);
        return navigation;
    }

    internal static MoveState Step(GroundMoveContext context, MoveState body, MoveCommand command)
    {
        var frame = context.NavigationFrame;
        var binding = context.ResolvedBinding;
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var queries));
        using (queries)
        {
            var result = binding.Step(new(body with { Position = body.Position - frame.PhysicsOrigin }, frame, null),
                command, ContactTerrainScene.Dt, queries!);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            return result.State.State with { Position = result.State.State.Position + frame.PhysicsOrigin };
        }
    }
}

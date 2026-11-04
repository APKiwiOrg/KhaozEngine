using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainLiveRefusalTests
{
    [Theory]
    [InlineData("roof")]
    [InlineData("foreign-solid")]
    [InlineData("water")]
    [InlineData("unsupported-landing")]
    public void LiveChangesCannotBorrowTheBakedTerrainVerdict(string fault)
    {
        using var original = new ContactTerrainScene(4);
        var nav = ContactTerrainRuntimeTests.Load(original);
        var grid = nav.Space.Layers[0];
        var (sx, sz) = grid.CellOf(original.From.X, original.From.Z);
        var (tx, tz) = grid.CellOf(original.To.X, original.To.Z);
        var link = nav.Graph.Links.Single(l => l.Kind == NavLinkKind.Drop && l.FromX == sx && l.FromZ == sz && l.ToX == tx && l.ToZ == tz);
        var traversal = new NavDropTraversal(link, original.From, original.To);
        var body = new MoveState
        {
            Position = original.From + Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1
        };
        var input = new PlayerPathInput(0.7f);
        Assert.Equal(RangeMoveStatus.Dropping, ContactDropPreview.Prove(nav, traversal, body,
            ContactTerrainScene.Tuning, false, ContactTerrainScene.Dt, original.Context, new(), input));
        var document = ContactTerrainScene.Document(4, profile: x => x <= 1 ? 4 : x >= 2 ? 0 : 4 * (2 - x));
        if (fault == "unsupported-landing")
            for (int z = 0; z <= 3; z++) for (int x = 2; x <= 5; x++) document.SetCornerHeightCm(x, z, 0, -200);
        using var changed = new ContactTerrainScene(4, document: document,
            water: fault == "water" ? MovementWaterField.Level("world", 1, -1) : null,
            extra: (world, builder) =>
            {
                if (fault is not ("roof" or "foreign-solid")) return;
                Vector3 centre = fault == "roof" ? new(1.5f, 3, -1.375f) : new(1.8f, 0.75f, -1.375f);
                Vector3 half = fault == "roof" ? new(0.2f, 0.1f, 0.4f) : new(0.2f, 0.3f, 0.4f);
                var handle = world.AddStatic(new BoxShape(half), Pose.At(centre));
                builder.Add(handle, 0, int.MaxValue, new("world", "runtime-obstruction"), centre - half, centre + half);
            });
        var drop = new ContactDropFollower(nav, traversal);
        var blocked = drop.Tick(body, ContactTerrainScene.Tuning, false, ContactTerrainScene.Dt, changed.Context,
            input, out bool complete, out bool replan);
        Assert.Equal(RangeMoveStatus.Blocked, blocked.Status);
        Assert.Equal(Vector2.Zero, blocked.WorldDirection);
        Assert.False(complete || replan);
    }
}

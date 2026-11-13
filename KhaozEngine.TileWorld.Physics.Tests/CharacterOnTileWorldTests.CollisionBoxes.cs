using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using static KhaozEngine.Tests.TileWorld.Physics.TileWorldPhysicsTestData;

namespace KhaozEngine.Tests.TileWorld.Physics;

// A body against a bench built from authored collision boxes rather than one box over its 2x1 footprint: it stands on
// the seat at the seat's own top, the back stops it, and where the footprint box used to stand there is nothing. The
// footprint box would have held every body up at the back's 0.94 m and stopped every walk at the footprint's edge, so
// each case below fails against it. Issue https://github.com/APKiwiOrg/KhaozEngine/issues/1782.
public partial class CharacterOnTileWorldTests
{
    const float West = MathF.PI / 2f;

    // The seat's tile z range and the back's north face, from BoxedBenchWorld: the seat runs from z 20.08 to 20.68, the
    // back from 20.68 to 20.8, and both end at x 20.2 and 21.8, inside a footprint from x 20 to 22 and z 20 to 21.
    const float SeatFront = BenchZ + 0.08f, BackNorthFace = BenchZ + 0.8f, BoxesEastEnd = BenchX + 1.8f;

    [Fact]
    public void ABodyStandsOnABenchSeatAtTheSeatTop()
    {
        using var scene = new Scene(BoxedBenchWorld());
        // Over the seat, 0.15 m in from its front edge and 0.45 m clear of the back, so the capsule touches the seat top
        // alone.
        float tileZ = SeatFront + 0.15f;
        MoveState body = scene.PlaceAt(new Vector3(BenchX + 1f, SeatTop + Character.CapsuleHalfHeight, -tileZ));

        for (int tick = 0; tick < 240; tick++)
        {
            body = scene.Walk(body, North, 1, move: 0f);
            float feet = scene.Absolute(body).Y - Character.CapsuleHalfHeight;
            Assert.True(MathF.Abs(feet - SeatTop) <= 0.02f, $"tick {tick}: feet at {feet}, seat top at {SeatTop}");
        }
        Assert.True(body.Grounded, "not standing on the seat");
        Assert.Equal(tileZ, -scene.Absolute(body).Z, 0.02f);
    }

    [Fact]
    public void ABenchBackStopsABodyInsideTheOldFootprint()
    {
        using var scene = new Scene(BoxedBenchWorld());

        Vector3 stopped = scene.Absolute(scene.Walk(scene.Place(BenchX + 1f, BenchZ + 3.5f), South, 120));

        // The back's north face is 0.2 m inside the footprint's north edge at z 21, so a body stopped by the back stands
        // a radius past that face and short of where the footprint box would have stopped it, a radius past z 21.
        float tileZ = -stopped.Z;
        Assert.True(tileZ >= BackNorthFace + Character.CapsuleRadius - 0.01f, $"crossed the back to tile z {tileZ}");
        Assert.True(tileZ <= BackNorthFace + Character.CapsuleRadius + 0.1f, $"stopped short at tile z {tileZ}");
        Assert.Equal(BenchX + 1f, stopped.X, 0.05f);
    }

    [Fact]
    public void ABodyWalksIntoTheEmptyEndOfTheFootprintUpToTheSeat()
    {
        using var scene = new Scene(BoxedBenchWorld());

        // Along the seat's middle row from the east, where the boxes end 0.2 m short of the footprint's east edge.
        Vector3 stopped = scene.Absolute(scene.Walk(scene.Place(BenchX + 5.5f, BenchZ + 0.4f), West, 120));

        Assert.True(stopped.X >= BoxesEastEnd + Character.CapsuleRadius - 0.01f, $"walked into the seat, at x {stopped.X}");
        Assert.True(stopped.X <= BoxesEastEnd + Character.CapsuleRadius + 0.1f, $"stopped short at x {stopped.X}");
        Assert.Equal(BenchZ + 0.4f, -stopped.Z, 0.05f);
        // The seat is above the step height, so the body stays on the grass rather than climbing it.
        Assert.Equal(Character.CapsuleHalfHeight, stopped.Y, 0.02f);
    }
}

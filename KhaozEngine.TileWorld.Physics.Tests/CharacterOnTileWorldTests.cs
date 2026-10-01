using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using static KhaozEngine.Tests.TileWorld.Physics.TileWorldPhysicsTestData;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>A character driven through <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
/// over a tile world's registered colliders, its floor, normal and water samplers: it climbs a slope, passes a door,
/// is stopped by walls and blocked tiles, walks under a deck and stands on it, and wades a river.</summary>
public class CharacterOnTileWorldTests
{
    const float Dt = 1f / 60f;

    // The engine's walking character: walk 6 m/s, a 1.8 m capsule of radius 0.4, a 0.4 m step and a 45 degree slope.
    static readonly MoveTuning Character = MoveTuning.Default;

    // Camera yaws that make a forward command walk along a compass direction. Tile north is world -z.
    const float North = 0f, South = MathF.PI, East = -MathF.PI / 2f;

    // The flat face a wall box shows a body: half the default wall thickness out from the edge it stands on.
    static readonly float WallFace = new TileColliderOptions().WallThickness / 2f;

    // The origin the rebased cases use. Small enough that the ramp walker stays over the ramp's mesh in either space,
    // and high enough against its x that a registration which forgot the origin would stand that mesh about a metre
    // above the floor the body walks on. Its z moves the river three rows, and its height puts the feet 2.5 m off,
    // so a medium wrapper that drops either offset reads the wader as dry or swimming.
    static readonly Vector3 RebasedOrigin = new(3.5f, 2.5f, -2.75f);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABodyWalksUpASlopeAndStaysOnTheGround(bool rebased)
    {
        using var scene = new Scene(RampWorld(), rebased ? RebasedOrigin : Vector3.Zero);
        MoveState body = scene.Place(5.5f, 30.5f);
        float startFeet = scene.Absolute(body).Y - Character.CapsuleHalfHeight;

        for (int tick = 0; tick < 120; tick++)
        {
            body = scene.Walk(body, East, 1);
            Vector3 at = scene.Absolute(body);
            float floor = scene.Colliders.Ground.HeightAt(at.X, at.Z);
            Assert.True(body.Grounded, $"tick {tick}: airborne at {at}");
            Assert.True(MathF.Abs(at.Y - Character.CapsuleHalfHeight - floor) <= 0.02f,
                $"tick {tick}: feet at {at.Y - Character.CapsuleHalfHeight}, floor at {floor}");
        }

        Vector3 end = scene.Absolute(body);
        // Two seconds east at walking pace, still on the row it started on, and risen with the ramp.
        Assert.True(end.X > 5.5f + 8f, $"only reached x {end.X}");
        Assert.Equal(30.5f, -end.Z, 0.05f);
        Assert.True(end.Y - Character.CapsuleHalfHeight - startFeet > 3f, $"rose only to {end.Y} from feet {startFeet}");
    }

    [Fact]
    public void ACapsulePassesADoorwayAndIsStoppedByTheWallBesideIt()
    {
        using var scene = new Scene(DoorwayWorld());
        float doorCentre = DoorX + 0.5f, besideTheDoor = DoorX - 4.5f;

        // Through the 1 m gap between the wall boxes on either side of the door, which a 0.8 m capsule clears.
        MoveState through = scene.Walk(scene.Place(doorCentre, DoorRow - 3.5f), North, 120);
        Vector3 past = scene.Absolute(through);
        Assert.True(-past.Z > DoorRow + 2f, $"stopped at tile z {-past.Z}, short of the door");
        Assert.Equal(doorCentre, past.X, 0.05f);

        // The same walk four tiles west runs into a wall and stays south of it.
        MoveState blocked = scene.Walk(scene.Place(besideTheDoor, DoorRow - 3.5f), North, 120);
        Vector3 stopped = scene.Absolute(blocked);
        AssertStoppedSouthOfTheWall(stopped);
        Assert.Equal(besideTheDoor, stopped.X, 0.05f);
    }

    [Fact]
    public void AWallBlocksFromBothSides()
    {
        using var scene = new Scene(DoorwayWorld());
        // East of the door, on a different wall box from the doorway test's blocked walk.
        const float x = WallRunLastX - 3.5f;

        Vector3 fromSouth = scene.Absolute(scene.Walk(scene.Place(x, DoorRow - 3.5f), North, 120));
        AssertStoppedSouthOfTheWall(fromSouth);

        Vector3 fromNorth = scene.Absolute(scene.Walk(scene.Place(x, DoorRow + 3.5f), South, 120));
        float tileZ = -fromNorth.Z;
        Assert.True(tileZ >= DoorRow + WallFace + Character.CapsuleRadius - 0.01f, $"crossed the wall to tile z {tileZ}");
        Assert.True(tileZ <= DoorRow + WallFace + Character.CapsuleRadius + 0.1f, $"stopped short at tile z {tileZ}");
        Assert.Equal(x, fromNorth.X, 0.05f);
    }

    [Fact]
    public void ABodyCanWalkUnderADeckAndStandOnIt()
    {
        using var scene = new Scene(HighDeckWorld());
        float deckCentre = HighDeckX + 1.5f, deckTop = HighDeckHeight;
        // The deck clears a standing body, so its walk surface box is all above the capsule's top.
        TileCollider deck = Assert.Single(scene.Colliders.Colliders, c => c.Kind == TileColliderKind.WalkSurface);
        float deckBottom = deck.Pose.Position.Y - Assert.IsType<BoxShape>(deck.Shape).HalfExtents.Y;
        Assert.True(deckBottom > 2f * Character.CapsuleHalfHeight, $"deck bottom {deckBottom}");

        // Under: straight across beneath it, on the drawn floor the whole way.
        MoveState under = scene.Place(deckCentre, HighDeckZ - 3.5f);
        for (int tick = 0; tick < 120; tick++)
        {
            under = scene.Walk(under, North, 1);
            Assert.Equal(Character.CapsuleHalfHeight, scene.Absolute(under).Y, 0.01f);
        }
        Assert.True(-scene.Absolute(under).Z > HighDeckZ + 3f + Character.CapsuleRadius,
            $"stopped at tile z {-scene.Absolute(under).Z} under the deck");

        // On: a body set on the deck stays at its top for four seconds, standing and then walking a metre along it,
        // while the floor sampler below still reports the grass. The deck holds it up only as a physics static.
        Assert.Equal(0f, scene.Colliders.Ground.HeightAt(deckCentre, -(HighDeckZ + 1.5f)), 1e-4f);
        MoveState on = scene.PlaceAt(new Vector3(deckCentre, deckTop + Character.CapsuleHalfHeight, -(HighDeckZ + 1.5f)));
        for (int tick = 0; tick < 240; tick++)
        {
            on = scene.Walk(on, North, 1, move: tick >= 230 ? 1f : 0f);
            Assert.True(MathF.Abs(scene.Absolute(on).Y - Character.CapsuleHalfHeight - deckTop) <= 0.02f,
                $"tick {tick}: feet at {scene.Absolute(on).Y - Character.CapsuleHalfHeight}, deck top at {deckTop}");
        }
        Assert.True(on.Grounded, "not standing on the deck");
        Assert.True(-scene.Absolute(on).Z > HighDeckZ + 2f, $"did not walk along the deck, at tile z {-scene.Absolute(on).Z}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WadingSlowsTheBody(bool rebased)
    {
        using var scene = new Scene(BridgedRiverWorld(), rebased ? RebasedOrigin : Vector3.Zero);
        float riverRow = RiverFirstRow + 1.5f;   // the flat bed of the river, a metre down

        MoveState dry = scene.Place(2.5f, 5.5f);
        MoveState wet = scene.Place(2.5f, riverRow);
        Vector3 wetStart = scene.Absolute(wet);
        float feet = wetStart.Y - Character.CapsuleHalfHeight;
        Assert.True(scene.Colliders.Medium.MediumAt(wetStart.X, wetStart.Z, feet).InWater, "the river start is dry");
        float wade = CharacterMovement.WadeSpeedScale(wetStart.X, wetStart.Z, feet, Character,
                                                      scene.Colliders.Medium.MediumDelegate);
        Assert.True(wade < 0.8f, $"the river is too shallow to slow anyone, wade scale {wade}");

        float dryDistance = scene.Absolute(scene.Walk(dry, East, 60)).X - 2.5f;
        Vector3 wetEnd = scene.Absolute(scene.Walk(wet, East, 60));
        float wetDistance = wetEnd.X - 2.5f;

        Assert.True(dryDistance > 5f, $"dry body only covered {dryDistance}");
        Assert.Equal(dryDistance * wade, wetDistance, 0.1f);
        Assert.Equal(riverRow, -wetEnd.Z, 0.05f);
    }

    [Fact]
    public void ABlockedTileStopsTheBody()
    {
        using var scene = new Scene(VoidAndBlockedWorld());

        Vector3 stopped = scene.Absolute(scene.Walk(scene.Place(2.5f, 4.5f), East, 120));

        // Tile (6, 4) is blocked, so the capsule stops a radius short of its west side and stays on its row.
        Assert.True(stopped.X <= 6f - Character.CapsuleRadius + 0.01f, $"walked into the blocked tile, at x {stopped.X}");
        Assert.True(stopped.X >= 6f - Character.CapsuleRadius - 0.1f, $"stopped short at x {stopped.X}");
        Assert.Equal(4.5f, -stopped.Z, 0.05f);
    }

    [Fact]
    public void ABodyWalkingOffAPlateauIsStoppedAtATallBlockedCliff()
    {
        using var scene = new Scene(CliffWorld());
        float plateau = PlateauCm * 0.01f;

        MoveState body = scene.Place(CliffX - 4.5f, 30.5f);
        Assert.Equal(plateau, scene.Absolute(body).Y - Character.CapsuleHalfHeight, 0.02f);
        Vector3 stopped = scene.Absolute(scene.Walk(body, East, 120));

        // The cliff tile falls further than the blocked height, and its box still stands above the plateau, so the
        // capsule stops a radius short of the cliff's west face, on the plateau and on its row.
        Assert.True(stopped.X <= CliffX - Character.CapsuleRadius + 0.01f, $"crossed onto the cliff, at x {stopped.X}");
        Assert.True(stopped.X >= CliffX - Character.CapsuleRadius - 0.1f, $"stopped short at x {stopped.X}");
        Assert.Equal(plateau, stopped.Y - Character.CapsuleHalfHeight, 0.02f);
        Assert.Equal(30.5f, -stopped.Z, 0.05f);
    }

    static void AssertStoppedSouthOfTheWall(Vector3 at)
    {
        float tileZ = -at.Z;
        Assert.True(tileZ <= DoorRow - WallFace - Character.CapsuleRadius + 0.01f, $"crossed the wall to tile z {tileZ}");
        Assert.True(tileZ >= DoorRow - WallFace - Character.CapsuleRadius - 0.1f, $"stopped short at tile z {tileZ}");
    }

    // A tile world registered in a Bepu world, with the movement step's delegates. With a non-zero origin the world
    // is rebased before registration, bodies live in the world's coordinates, and the samplers are wrapped to add the
    // origin back to every coordinate they receive and take it off every height they answer.
    sealed class Scene : IDisposable
    {
        readonly BepuPhysicsWorld _world = new();
        readonly TileColliderRegistration _registration;
        readonly Func<float, float, float> _height;
        readonly Func<float, float, Vector3> _normal;
        readonly Func<float, float, float, MovementMedium> _medium;

        public Scene(TileWorldDocument document, Vector3 origin = default)
        {
            Colliders = TileWorldColliders.Build(document, Catalogs());
            if (origin != Vector3.Zero) _world.Rebase(origin);
            _registration = Colliders.AddTo(_world);

            TileGroundSampler ground = Colliders.Ground;
            TileMediumSampler water = Colliders.Medium;
            Vector3 o = _world.Origin;
            if (o == Vector3.Zero)
            {
                _height = ground.HeightDelegate;
                _normal = ground.NormalDelegate;
                _medium = water.MediumDelegate;
            }
            else
            {
                _height = (x, z) => ground.HeightAt(x + o.X, z + o.Z) - o.Y;
                _normal = (x, z) => ground.NormalAt(x + o.X, z + o.Z);
                _medium = (x, z, feetY) => water.MediumAt(x + o.X, z + o.Z, feetY + o.Y) is { InWater: true } m
                    ? new MovementMedium(m.WaterSurfaceY - o.Y, inWater: true, m.WadeSpeedScale)
                    : MovementMedium.Dry;
            }
        }

        public TileWorldColliders Colliders { get; }

        // A body standing on the floor at a tile point.
        public MoveState Place(float tileX, float tileZ)
        {
            float x = TileWorldSpace.WorldX(tileX, 1f), z = TileWorldSpace.WorldZ(tileZ, 1f);
            return PlaceAt(new Vector3(x, Colliders.Ground.HeightAt(x, z) + Character.CapsuleHalfHeight, z));
        }

        // A grounded body with its capsule centre at an absolute point, settled for a few idle ticks.
        public MoveState PlaceAt(Vector3 absoluteCentre)
        {
            var body = new MoveState { Position = absoluteCentre - _world.Origin, Grounded = true };
            return Walk(body, North, 10, move: 0f);
        }

        // Ticks a body forward along a camera yaw, at walking pace by default.
        public MoveState Walk(MoveState body, float yaw, int ticks, float move = 1f)
        {
            var command = new MoveCommand(new Vector2(0f, move), run: false, cameraYaw: yaw);
            for (int i = 0; i < ticks; i++)
                body = CharacterMovement.Step(body, command, Dt, _height, Character, _normal, _world, null, _medium);
            return body;
        }

        // A body's capsule centre in absolute document coordinates.
        public Vector3 Absolute(MoveState body) => body.Position + _world.Origin;

        public void Dispose()
        {
            _registration.Dispose();
            _world.Dispose();
        }
    }
}

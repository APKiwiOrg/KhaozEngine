using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.WaterPrismCensus;

// Authored engine fixtures, with the lake's metre tiles, centimetre grades and -0.37 m surface.
internal sealed class WaterPrismScenario : IDisposable
{
    internal static readonly string[] Names = ["bank-stand", "bank-along", "bank-across", "swim-entry",
        "deep-swim", "deep-tread", "flat-neighbour", "flat-control", "dry-control"];
    internal const float Dt = 1f / 30;
    internal static MoveTuning Tuning => new(2, 5, 0.75f, MathF.PI / 4, CapsuleRadius: 0.3f)
    {
        Controller = MovementControllerKind.Contact,
        Gravity = 25,
        StepHeight = 0.4f,
        FootRadiusFraction = 0.5f,
        SwimSpeed = 3,
        SwimBuoyancyStiffness = 8,
        SwimEnterDepthFraction = 0.65f,
        SwimExitDepthFraction = 0.55f,
        WadeMinSpeedScale = 0.6f,
        BackpedalSpeedScale = 0.65f,
        SwimSurfaceSubmersionFraction = 0.6f,
        Water = new(WaterTraversalMode.SurfaceSwimmer),
    };
    readonly BepuPhysicsWorld _world = new(Vector3.Zero);
    readonly TileColliderRegistration _installed;
    internal TileWorldColliders Colliders { get; }
    internal PlayerMoveSimulator Simulator { get; }
    internal RecordingWaterField Field { get; }
    internal PlayerMoveState Basis { get; }
    internal MoveCommand Command { get; }
    internal string Name { get; }

    internal WaterPrismScenario(string name)
    {
        if (Array.IndexOf(Names, name) < 0) throw new ArgumentOutOfRangeException(nameof(name));
        Name = name;
        bool bank = name.StartsWith("bank-", StringComparison.Ordinal);
        bool deep = name.StartsWith("deep-", StringComparison.Ordinal);
        float z = bank ? 236.365f : name == "swim-entry" ? 246.23f : deep ? 260.568f : 252.85f;
        int firstZ = bank || name == "dry-control" ? 236 : (int)z - 4;
        var doc = Document(name, firstZ);
        var catalogs = TileWorldCatalogs.LoadJson("""
            {"materials":[{"id":1,"name":"ground","kind":"Ground","color":"#446633"},
            {"id":3,"name":"water","kind":"Water","color":"#336699"}],"archetypes":[]}
            """, "prism-census");
        Colliders = TileWorldColliders.Build(doc, catalogs, "water-world");
        _installed = Colliders.AddTo(_world);
        var builder = _installed.CreateMovementRegistrationBuilder("water-world",
            new(new(63, -120, -321), new(129, 20, -191), -101, 10));
        OrdinaryMovementRegistration registration;
        using (var lease = _world.AcquireQueryReadLease()) registration = builder.Build(lease);
        Field = new(Colliders.Medium);
        Simulator = new(Colliders.MovementGroundHeight, Tuning, Colliders.MovementGroundNormal, null,
            _world, null, SamplerSpace.World, null, Field, registration,
            MovementBoundary.Rectangle(65, -319, 127, -193));
        bool swimming = deep || name is "flat-neighbour" or "flat-control";
        if (name == "dry-control") z = 234.5f;
        float y = swimming ? -0.52f : Colliders.MovementGroundHeight(110.375f, -z) + 0.75f;
        var basis = new PlayerMoveState
        {
            Move = new()
            {
                Position = new(110.375f, y, -z),
                Grounded = !swimming,
                Swimming = swimming,
                SpeedScale = 1
            }
        };
        // Settle the support disc before observing the fixed authority basis.
        for (int tick = 0; tick < 4; tick++) basis = Step(basis, MoveCommand.Idle);
        Basis = basis;
        Command = name switch
        {
            "bank-along" => new(Vector2.UnitX, false, 0),
            "bank-across" or "swim-entry" or "deep-swim" or "flat-neighbour" or "flat-control"
                => new(Vector2.UnitY, false, 0),
            _ => MoveCommand.Idle,
        };
    }

    internal PlayerMoveState Step(PlayerMoveState state, MoveCommand command)
    {
        using var read = Simulator.BeginExplicitRead(state);
        var next = Simulator.Step(state, command, Dt);
        if (Simulator.LastExplicitOutcome != MovementStepOutcome.Advanced)
            throw new InvalidOperationException($"{Name}: {Simulator.LastExplicitOutcome}");
        return next;
    }

    static TileWorldDocument Document(string name, int firstZ)
    {
        var doc = new TileWorldDocument { Id = "prism-census", TileSize = 1 };
        var region = RegionCoord.Of(110, firstZ);
        doc.GetOrCreateRegion(region);
        for (int z = region.OriginZ; z < region.OriginZ + 64; z++)
            for (int x = region.OriginX; x < region.OriginX + 64; x++) doc.SetUnderlay(x, z, 0, 1);
        for (int z = firstZ; z <= firstZ + 7; z++)
            for (int x = 106; x <= 116; x++)
            {
                int cm = name switch
                {
                    "bank-stand" or "bank-along" or "bank-across" or "dry-control" => z == firstZ ? -35 : -130,
                    "swim-entry" => -130 - 18 * (z - 246),
                    "deep-swim" or "deep-tread" => Math.Max(-537, -369 - 44 * (z - 260)),
                    "flat-neighbour" => -237 - 7 * Math.Max(0, z - 253),
                    _ => -237,
                };
                doc.SetCornerHeightCm(x, z, 0, checked((short)cm));
                if (x < 116 && z < firstZ + 7) doc.SetUnderlay(x, z, 0, 3);
            }
        // A distant rim corner selects the same lake level without importing a consumer world.
        doc.SetCornerHeightCm(106, firstZ, 0, -35);
        return doc;
    }

    public void Dispose() { Simulator.Dispose(); _installed.Dispose(); _world.Dispose(); }
}

using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
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
    readonly bool _contact;
    readonly TileColliderRegistration _installed;
    internal TileWorldColliders Colliders { get; }
    internal PlayerMoveSimulator Simulator { get; }
    internal RecordingWaterField Field { get; }
    /// <summary>The field that exposes the certificate, when the scenario forwards it.</summary>
    internal CertificateForwardingWaterField? Certificate { get; }
    internal PlayerMoveState Basis { get; }
    internal MoveCommand Command { get; }
    internal string Name { get; }

    /// <summary>A Contact scenario uses its own SurfaceSwimmer policy and requires every settling step to advance unless
    /// <paramref name="water"/> chooses another policy, whose settling steps may hold. The recording field hides the
    /// boundary reach certificate unless <paramref name="exposeCertificate"/> forwards it.</summary>
    internal WaterPrismScenario(string name, MovementControllerKind controller = MovementControllerKind.Contact,
        bool crowded = false, WaterTraversalMode? water = null, bool exposeCertificate = false)
    {
        if (Array.IndexOf(Names, name) < 0 && name != "shore-traverse") throw new ArgumentOutOfRangeException(nameof(name));
        Name = name;
        _contact = controller == MovementControllerKind.Contact;
        bool bank = name.StartsWith("bank-", StringComparison.Ordinal);
        bool deep = name.StartsWith("deep-", StringComparison.Ordinal);
        float z = name == "shore-traverse" ? 235.8f : bank ? 236.365f : name == "swim-entry" ? 246.23f : deep ? 260.568f : 252.85f;
        int firstZ = name == "shore-traverse" || bank || name == "dry-control" ? 236 : (int)z - 4;
        var doc = Document(name, firstZ);
        var catalogs = TileWorldCatalogs.LoadJson("""
            {"materials":[{"id":1,"name":"ground","kind":"Ground","color":"#446633"},
            {"id":3,"name":"water","kind":"Water","color":"#336699"}],"archetypes":[]}
            """, "prism-census");
        Colliders = TileWorldColliders.Build(doc, catalogs, "water-world");
        _installed = Colliders.AddTo(_world);
        if (crowded)
            for (int i = 0; i < 12; i++)
                foreach (float side in new[] { -1f, 1f })
                    _world.AddStatic(new BoxShape(new Vector3(0.2f, 0.5f, 0.2f)),
                        Pose.At(new(108 + i * 0.6f, 0.5f, -234.5f + side * 0.75f)));
        var builder = _installed.CreateMovementRegistrationBuilder("water-world",
            new(new(63, -120, -321), new(129, 20, -191), -101, 10));
        if (crowded)
        {
            var support = _world.AddStatic(new BoxShape(new Vector3(4, 0.1f, 1)), Pose.At(new(111, -0.1f, -234.5f)));
            builder.Add(support, 0, int.MaxValue, new("water-world", "cost-platform"),
                new(107, -0.2f, -235.5f), new(115, 0, -233.5f));
        }
        OrdinaryMovementRegistration registration;
        using (var lease = _world.AcquireQueryReadLease()) registration = builder.Build(lease);
        Field = new(Colliders.Medium);
        var policy = water is { } mode ? new WaterTraversalPolicy(mode) : Tuning.Water;
        var tuning = Tuning with { Controller = controller, Water = _contact ? policy : default };
        Certificate = exposeCertificate ? new CertificateForwardingWaterField(Field, Colliders.Medium) : null;
        IMovementWaterField field = Certificate ?? (IMovementWaterField)Field;
        Simulator = new(Colliders.MovementGroundHeight, tuning, Colliders.MovementGroundNormal, null,
            _world, Colliders.Medium.MediumDelegate, SamplerSpace.World, null, field, registration,
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
        for (int tick = 0; tick < 4; tick++) basis = water is null ? Step(basis, MoveCommand.Idle) : Step(basis, MoveCommand.Idle, out _);
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
        var next = Step(state, command, out MovementStepOutcome? outcome);
        if (_contact && outcome != MovementStepOutcome.Advanced)
            throw new InvalidOperationException($"{Name}: {outcome}");
        return next;
    }

    /// <summary>One authority operation under a fresh read, with its outcome, whatever the outcome is.</summary>
    internal PlayerMoveState Step(PlayerMoveState state, MoveCommand command, out MovementStepOutcome? outcome)
    {
        using var read = Simulator.BeginExplicitRead(state);
        var next = Simulator.Step(state, command, Dt);
        outcome = Simulator.LastExplicitOutcome;
        return next;
    }

    internal void Rebase(Vector3 origin)
    {
        _world.Rebase(origin);
        Simulator.Frame = WorldFrame.Nearest(origin.X, origin.Z);
    }

    static TileWorldDocument Document(string name, int firstZ)
    {
        var doc = new TileWorldDocument { Id = "prism-census", TileSize = 1 };
        var region = RegionCoord.Of(110, firstZ);
        doc.GetOrCreateRegion(region);
        for (int z = region.OriginZ; z < region.OriginZ + 64; z++)
            for (int x = region.OriginX; x < region.OriginX + 64; x++) doc.SetUnderlay(x, z, 0, 1);
        int depth = name == "shore-traverse" ? 16 : 7;
        for (int z = firstZ; z <= firstZ + depth; z++)
            for (int x = 106; x <= 116; x++)
            {
                int cm = name switch
                {
                    "bank-stand" or "bank-along" or "bank-across" or "dry-control" => z == firstZ ? -35 : -130,
                    "shore-traverse" => -35 - 18 * (z - 236),
                    "swim-entry" => -130 - 18 * (z - 246),
                    "deep-swim" or "deep-tread" => Math.Max(-537, -369 - 44 * (z - 260)),
                    "flat-neighbour" => -237 - 7 * Math.Max(0, z - 253),
                    _ => -237,
                };
                doc.SetCornerHeightCm(x, z, 0, checked((short)cm));
                if (x < 116 && z < firstZ + depth) doc.SetUnderlay(x, z, 0, 3);
            }
        // A distant rim corner selects the same lake level without importing a consumer world.
        doc.SetCornerHeightCm(106, firstZ, 0, -35);
        return doc;
    }

    public void Dispose() { Simulator.Dispose(); _installed.Dispose(); _world.Dispose(); }
}

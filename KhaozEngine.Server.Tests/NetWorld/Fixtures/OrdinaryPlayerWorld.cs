using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

internal sealed class OrdinaryPlayerWorld : IDisposable
{
    public const float Dt = 0.1f;
    public static MoveTuning Tuning => MoveTuning.Default with
    {
        Controller = MovementControllerKind.Contact,
        Water = new(WaterTraversalMode.SurfaceSwimmer, 7),
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
        WalkSpeed = 4,
        RunSpeed = 8,
        Gravity = 20,
        StepHeight = 0.4f
    };
    public ObservedPhysicsWorld Physics { get; } = new();
    public OrdinaryMovementRegistration Registration { get; }
    public IMovementWaterField? Field { get; }
    public MovementBoundary Boundary { get; }
    public Func<float, float, float> Ground { get; }
    public Func<float, float, Vector3>? Normal { get; }
    public WorldFrame Frame { get; }
    readonly ContactPublicationTerrain? terrainSource;
    public OrdinaryPlayerWorld(Vector3 origin = default, bool stairs = false, bool roof = false,
        bool water = false, float groundY = 0, float island = 20, IMovementWaterField? field = null, float? dropHeight = null, bool terrain = false)
    {
        Frame = WorldFrame.Nearest(origin.X, origin.Z);
        Physics.Rebase(origin);
        Boundary = MovementBoundary.Rectangle(origin.X - 20, origin.Z - 20, origin.X + 20, origin.Z + 20);
        Field = field ?? (water ? MovementWaterField.Level("world", 0, -4) : null);
        if (terrain && dropHeight is { } terrainHeight)
        {
            terrainSource = new(Physics, terrainHeight);
            Ground = terrainSource.Colliders.MovementGroundHeight;
            Normal = terrainSource.Colliders.MovementGroundNormal;
            using var terrainLease = Physics.AcquireQueryReadLease();
            Registration = terrainSource.Installed.CreateMovementRegistrationBuilder("world",
                new(new(-240, -10, -240), new(240, 12, 240), 0, terrainHeight)).Build(terrainLease);
            return;
        }
        Ground = dropHeight is { } height ? (x, _) => x <= 4 ? height : 0 : (_, _) => groundY;
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", Physics,
            new(new(origin.X - island, -8, origin.Z - island), new(origin.X + island, 8, origin.Z + island), groundY, dropHeight ?? groundY));
        Add("floor", new(origin.X - island, groundY - 0.25f, origin.Z - island),
            new(origin.X + island, groundY, origin.Z + island));
        if (dropHeight is { } top) Add("ledge", new(-8, 0, -8), new(4, top, 8));
        if (stairs)
            for (int i = 0; i < 4; i++)
                Add($"step-{i}", new(origin.X + 0.4f + i * 0.25f, groundY, origin.Z - 4),
                    new(origin.X + 4, groundY + (i + 1) * 0.15f, origin.Z + 4));
        if (roof) Add("roof", new(origin.X - 4, 1.5005f, origin.Z - 4), new(origin.X + 4, 1.75f, origin.Z + 4));
        using var lease = Physics.AcquireQueryReadLease();
        Registration = builder.Build(lease);

        void Add(string owner, Vector3 min, Vector3 max)
        {
            var handle = Physics.AddStatic(new BoxShape((max - min) * 0.5f), Pose.At((min + max) * 0.5f - origin));
            builder.Add(handle, 0, int.MaxValue, new("world", owner), min, max);
        }
    }
    public PlayerMoveSimulator Simulator(MoveTuning? tuning = null, SamplerSpace space = SamplerSpace.World,
        WorldBounds? bounds = null, MovementBoundary? boundary = null) => new(Ground, tuning ?? Tuning,
        Normal, bounds, Physics, null, space, null, Field, Registration, boundary ?? Boundary)
        { Frame = Frame };
    public EffectiveMovementBinding Reference(MoveTuning? tuning = null) => EffectiveMovementBinding.Ordinary(
        tuning ?? Tuning, Physics, Registration, Ground, Normal, Boundary, Field);
    public PlayerMoveState Initial(float x = 0, float z = 0, float? y = null) =>
        new()
        {
            Move = new() { Position = new(x, y ?? Ground(x, z) + 0.75f, z), Grounded = true },
            FrameAnchor = new(Frame.Anchor.X, Frame.Anchor.Z)
        };
    public void Dispose() => Physics.Dispose();
}

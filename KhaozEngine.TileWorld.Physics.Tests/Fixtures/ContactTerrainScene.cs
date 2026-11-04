using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.Movement;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal sealed class ContactTerrainScene : IDisposable
{
    internal static MoveTuning Tuning => new(2, 5, 0.75f, MathF.PI / 4, CapsuleRadius: 0.3f)
    { Controller = MovementControllerKind.Contact, Water = new(WaterTraversalMode.SurfaceSwimmer) };
    internal const float Dt = 1f / 30;
    internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
    internal TileWorldColliders Colliders { get; }
    internal TileColliderRegistration Installed { get; }
    internal OrdinaryMovementRegistration Registration { get; }
    internal GroundMoveContext Context { get; }
    internal Vector3 From { get; }
    internal Vector3 To => new(2.375f, 0, -1.375f);
    internal IMovementAnalyticSupportSource Source => Registration.AnalyticSupport!;

    internal ContactTerrainScene(float height, bool identity = true,
        Action<BepuPhysicsWorld, OrdinaryMovementRegistration.Builder>? extra = null, TileWorldDocument? document = null)
    {
        From = new(0.625f, height, -1.375f);
        var doc = document ?? Document(height);
        var catalogs = TileWorldCatalogs.LoadJson("{\"materials\":[],\"archetypes\":[]}", "contact-terrain-tests");
        Colliders = TileWorldColliders.Build(doc, catalogs, "world");
        Installed = Colliders.AddTo(World);
        var builder = Installed.CreateMovementRegistrationBuilder("world",
            new(new(-20, -120, -20), new(20, 20, 20), -101, 10));
        extra?.Invoke(World, builder);
        using (var lease = World.AcquireQueryReadLease()) Registration = builder.Build(lease);
        Context = new(identity ? Colliders.MovementGroundHeight : (x, z) => Colliders.MovementGroundHeight(x, z), Colliders.MovementGroundNormal, World, null, null, null,
            null, Registration, MovementBoundary.Rectangle(0, -3, 5, 0));
    }

    internal static TileWorldDocument Document(float height)
    {
        var doc = TileWorldMovementNavigationTests.Drawn(0, 0, 5, 3);
        for (int z = 0; z <= 3; z++)
            for (int x = 0; x <= 1; x++) doc.SetCornerHeightCm(x, z, 0, checked((short)MathF.Round(height * 100)));
        return doc;
    }

    public void Dispose() { Context.Dispose(); Installed.Dispose(); World.Dispose(); }
}

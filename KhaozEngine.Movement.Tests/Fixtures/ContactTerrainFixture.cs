using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Movement.Fixtures;

/// <summary>Continuous triangle ground, with a one-metre steep band between genuine upper and lower patches.</summary>
internal sealed class ContactTerrainFixture : IDisposable, IMovementAnalyticSupportSource
{
    readonly Func<float, float, float> _height;
    readonly Func<float, float, Vector3> _normal;
    readonly Vector3[] _vertices;
    readonly int[] _indices = [0, 1, 4, 1, 5, 4, 1, 2, 5, 2, 6, 5, 2, 3, 6, 3, 7, 6];
    internal static MoveTuning Tuning => new(2, 5, 0.75f, MathF.PI / 4, CapsuleRadius: 0.3f,
        StepHeight: 0.4f, WadeMinSpeedScale: 0.6f, SwimSpeed: 3)
    { Controller = MovementControllerKind.Contact, Water = new(WaterTraversalMode.SurfaceSwimmer) };
    internal static PhysicsNavBakeOptions Options => new(-1, -0.75f, 2, 0.75f, 0.25f, 10, 14,
        Tuning.MaxSlopeRadians, 128, 512);
    internal OrdinaryContactScene Scene { get; }
    internal GroundMoveContext Context { get; }
    internal PhysicsNavBake Capture { get; }
    internal Vector3 From { get; }
    internal Vector3 To => new(1.375f, 0, 0.125f);

    internal ContactTerrainFixture(float height, bool terrainEvidence = false, NavAreaClassifier? classify = null)
    {
        From = new(-0.375f, height, 0.125f);
        _height = (x, _) => x <= 0 ? height : x >= 1 ? 0 : height * (1 - x);
        _normal = (x, _) => x <= 0 || x >= 1 ? Vector3.UnitY : Vector3.Normalize(new(height, 1, 0));
        _vertices = [new(-8, height, -8), new(0, height, -8), new(1, 0, -8), new(8, 0, -8),
            new(-8, height, 8), new(0, height, 8), new(1, 0, 8), new(8, 0, 8)];
        // Mirror certified tile ground. Capture reads the genuine mesh, while the registered movement selection
        // uses its exact height field and normal, as the installed terrain producer does.
        Scene = new(Tuning, _height, _normal, extent: new(new(-20, -10, -20), new(20, 12, 20), -10, 10),
            install: (world, builder) =>
            {
                var handle = world.AddStatic(new TriangleMeshShape(_vertices, _indices), Pose.Identity);
                builder.Add(handle, 0, 5, new("world", "terrain"), new(-8, 0, -8), new(8, height, 8));
                builder.Exclude(handle);
                if (terrainEvidence) builder.WithAnalyticSupport(this);
            });
        Context = ContactNavigationFixture.Context(Scene);
        Capture = PhysicsNavBake.Capture(Context, Options with { CaptureTerrainDescentEvidence = terrainEvidence }, classify ?? (_ => 0));
    }

    public bool Matches(Func<float, float, float> height, Func<float, float, Vector3>? normal, OrdinaryGroundSpace space)
        => height == _height && normal == _normal && space == OrdinaryGroundSpace.World;

    public MovementAvailability Describe(Vector2 axis, out MovementSurfaceFeature feature)
    {
        int band = axis.X <= 0 ? 0 : axis.X >= 1 ? 2 : 1;
        float left = band == 0 ? -8 : band == 1 ? 0 : 1;
        float width = band == 0 ? 8 : band == 1 ? 1 : 7;
        int triangle = band * 2 + ((axis.X - left) / width + (axis.Y + 8) / 16 <= 1 ? 0 : 1);
        return GetFeature(new("terrain", triangle, 0), out feature);
    }

    public MovementAvailability GetFeature(MovementSurfaceFeatureKey key, out MovementSurfaceFeature feature)
    {
        feature = default;
        if (key.Source != "terrain" || key.Part != 0 || key.Address is < 0 or >= 6) return MovementAvailability.Unresolved;
        int offset = (int)key.Address * 3;
        Vector3 a = _vertices[_indices[offset]], b = _vertices[_indices[offset + 1]], c = _vertices[_indices[offset + 2]];
        feature = new(key, new("world", "terrain"), a, b, c, _normal((a.X + b.X + c.X) / 3, 0));
        return MovementAvailability.Known;
    }

    public bool AreAdjacent(MovementSurfaceFeatureKey first, MovementSurfaceFeatureKey second)
    {
        if (GetFeature(first, out var a) != MovementAvailability.Known || GetFeature(second, out var b) != MovementAvailability.Known)
            return false;
        int shared = 0;
        foreach (var p in new[] { a.A, a.B, a.C })
            if (p == b.A || p == b.B || p == b.C) shared++;
        return shared >= 2;
    }

    public void Dispose() { Capture.Dispose(); Context.Dispose(); Scene.Dispose(); }
}

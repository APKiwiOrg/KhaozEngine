using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.Movement.Fixtures;

internal sealed class FlatArrivalScene : IDisposable, IMovementFlatAnalyticSupportSource, IMovementSupportedAnalyticSupportSource
{
    internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
    internal GroundMoveContext Context { get; }
    internal MoveTuning Tuning => DefaultTuning;
    internal static MoveTuning DefaultTuning => new(4, 6, 0.75f, 20f * MathF.PI / 180f,
        CapsuleRadius: 0.25f, TractionHysteresisRadians: 0f)
    { Controller = MovementControllerKind.Contact, Water = new(WaterTraversalMode.DryOnly) };
    readonly Func<float, float, float> _height;
    readonly Func<float, float, Vector3> _normal;
    readonly MovementSurfaceFeature _feature;
    readonly IPhysicsWorldQueryView? _suppliedView;
    readonly float? _slope;
    readonly float? _minimumNormalY;
    readonly Vector2? _heightBounds;
    internal FlatArrivalScene(Func<float, float, float>? height = null,
        Func<float, float, Vector3>? normal = null, IMovementWaterField? water = null,
        bool provenance = true, MovementSurfaceFeature? feature = null, Vector3? origin = null,
        Action<BepuPhysicsWorld, OrdinaryMovementRegistration.Builder>? install = null,
        MovementBoundary? boundary = null, OrdinaryMovementExtent? extent = null,
        Func<BepuPhysicsWorld, IPhysicsWorldQueryView>? movementQueries = null,
        Func<float, float, Vector2>? clampXz = null, float? slope = null,
        float? minimumNormalY = null, Vector2? heightBounds = null)
    {
        _slope = slope;
        _minimumNormalY = minimumNormalY;
        _heightBounds = heightBounds;
        _height = height ?? (slope.HasValue ? (x, _) => x * slope.Value : (_, _) => 0);
        _normal = normal ?? (slope.HasValue ? (_, _) => Vector3.Normalize(new(-slope.Value, 1, 0)) : (_, _) => Vector3.UnitY);
        _feature = feature ?? new(new("flat", 0, 0), new("world", "terrain"), new(-100, 0, -100),
            new(100, 0, -100), new(0, 0, 100), Vector3.UnitY);
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", World,
            extent ?? new(new(-20, -4, -20), new(20, 4, 20), -2, 2));
        if (provenance) builder.WithAnalyticSupport(this);
        install?.Invoke(World, builder);
        if (origin.HasValue) World.Rebase(origin.Value);
        OrdinaryMovementRegistration registration;
        using (var lease = World.AcquireQueryReadLease()) registration = builder.Build(lease);
        _suppliedView = movementQueries?.Invoke(World);
        Context = new(_height, _normal, World, clampXz, null, _suppliedView, water, registration,
            boundary ?? MovementBoundary.Rectangle(-20, -20, 20, 20));
    }

    internal static Vector2 ThrowingClamp(float x, float z) => throw new InvalidOperationException("Contact read the legacy clamp.");

    public bool Matches(Func<float, float, float> height, Func<float, float, Vector3>? normal, OrdinaryGroundSpace space)
        => height == _height && normal == _normal && space == OrdinaryGroundSpace.World;
    public MovementAvailability Describe(Vector2 absoluteAxis, out MovementSurfaceFeature feature)
        => GetFeature(new("flat", 0, 0), out feature);
    public MovementAvailability GetFeature(MovementSurfaceFeatureKey key, out MovementSurfaceFeature feature)
    {
        feature = _feature;
        return MovementAvailability.Known;
    }
    public bool AreAdjacent(MovementSurfaceFeatureKey first, MovementSurfaceFeatureKey second) => first == second;
    public MovementAvailability CertifyFlatRegion(Vector2 minimum, Vector2 maximum, out float height)
    {
        height = 0;
        return _slope.HasValue ? MovementAvailability.Unresolved : MovementAvailability.Known;
    }
    public MovementAvailability CaptureSupportedRegion(Vector2 minimum, Vector2 maximum, out IMovementSupportedGroundRegion? region)
    {
        region = _slope.HasValue ? new SlopeRegion(_height, _normal, minimum, maximum, _minimumNormalY, _heightBounds) : null;
        return region is null ? MovementAvailability.Unresolved : MovementAvailability.Known;
    }
    sealed class SlopeRegion(Func<float, float, float> height, Func<float, float, Vector3> normal,
        Vector2 minimum, Vector2 maximum, float? minimumNormalY, Vector2? heightBounds) : IMovementSupportedGroundRegion
    {
        public float MinimumHeight => heightBounds?.X ?? MathF.Min(height(minimum.X, minimum.Y), height(maximum.X, maximum.Y));
        public float MaximumHeight => heightBounds?.Y ?? MathF.Max(height(minimum.X, minimum.Y), height(maximum.X, maximum.Y));
        public float MinimumNormalY => minimumNormalY ?? normal(0, 0).Y;
        public MovementAvailability Sample(Vector2 axis, out float value, out Vector3 n)
        {
            value = height(axis.X, axis.Y);
            n = normal(axis.X, axis.Y);
            return axis.X >= minimum.X && axis.X <= maximum.X && axis.Y >= minimum.Y && axis.Y <= maximum.Y
                ? MovementAvailability.Known : MovementAvailability.Unresolved;
        }
    }
    public void Dispose() { Context.Dispose(); _suppliedView?.Dispose(); World.Dispose(); }
}

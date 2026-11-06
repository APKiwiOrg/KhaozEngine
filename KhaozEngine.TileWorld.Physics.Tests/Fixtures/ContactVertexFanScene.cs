using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal sealed class ContactVertexFanScene : IDisposable
{
    internal static PhysicsNavBakeOptions Options => new(0, -4, 0.25f, -3.75f, 0.25f, 2, 4,
        ContactTerrainScene.Tuning.MaxSlopeRadians, 4, 64);
    readonly BepuPhysicsWorld _world = new(Vector3.Zero);
    internal GroundMoveContext Context { get; }

    internal ContactVertexFanScene(string? fault = null)
    {
        var source = new FanSource(fault);
        var vertices = source.Installed.SelectMany(f => new[] { f.C, f.B, f.A }).ToArray();
        var mesh = _world.AddStatic(new TriangleMeshShape(vertices, Enumerable.Range(0, vertices.Length).ToArray()), Pose.Identity);
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", _world,
            new(new(-1, -1, -5), new(1, 3, -3), -1, 3)).WithAnalyticSupport(source)
            .Add(mesh, 0, int.MaxValue, new("world", "ground"), new(0, 0, -4), new(0.25f, 0, -3.75f)).Exclude(mesh);
        OrdinaryMovementRegistration registration;
        using (var lease = _world.AcquireQueryReadLease()) registration = builder.Build(lease);
        Context = new(source.Height, source.Normal, _world, null, null, null, null, registration, null);
    }

    public void Dispose() { Context.Dispose(); _world.Dispose(); }

    sealed class FanSource : IMovementAnalyticSupportSource
    {
        static readonly Vector2 Axis = new(0.125f, -3.875f);
        readonly MovementSurfaceFeature[] _features;
        readonly string? _fault;
        internal MovementSurfaceFeature[] Installed { get; }
        internal readonly Func<float, float, float> Height = (_, _) => 0;
        internal readonly Func<float, float, Vector3> Normal = (_, _) => Vector3.UnitY;

        internal FanSource(string? fault)
        {
            _fault = fault;
            const float h = 0.0625f;
            Vector2[][] offsets = [
                [new(0,h),new(0,0),new(-h,h)], [new(0,0),new(-h,0),new(-h,h)],
                [new(h,0),new(0,0),new(0,h)], [new(0,0),new(0,-h),new(-h,0)],
                [new(h,0),new(h,-h),new(0,0)], [new(h,-h),new(0,-h),new(0,0)]];
            Vector3 At(Vector2 p) => new(Axis.X + p.X, 0, Axis.Y + p.Y);
            _features = offsets.Select((p, i) => new MovementSurfaceFeature(new("fan", 0, i), new("world", "analytic"),
                At(p[0]), At(p[1]), At(p[2]), Vector3.UnitY)).ToArray();
            if (fault == "overlap")
                _features[5] = _features[5] with { A = At(new(0, 0)), B = At(new(0, -h)), C = At(new(-h / 2, 0)) };
            if (fault == "coincident-owner") _features[5] = _features[5] with { Owner = new("world", "other-analytic") };
            Installed = fault switch
            {
                "duplicate" => [.. _features, _features[0]],
                "disconnected" => [_features[2], _features[3]],
                "gap" => [_features[1], _features[2], _features[3], _features[4]],
                "missing-canonical" => _features.Where(f => f.Key.Part != 4).ToArray(),
                _ => _features
            };
        }

        public bool Matches(Func<float, float, float> height, Func<float, float, Vector3>? normal, OrdinaryGroundSpace space) =>
            height == Height && normal == Normal && space == OrdinaryGroundSpace.World;
        public MovementAvailability Describe(Vector2 axis, out MovementSurfaceFeature feature)
        {
            feature = default;
            if (axis == Axis)
            {
                if (_fault == "unresolved") return MovementAvailability.Unresolved;
                feature = _features[_fault == "disconnected" ? 2 : 4];
                return MovementAvailability.Known;
            }
            foreach (var f in _features)
                if (axis == new Vector2((f.A.X + f.B.X + f.C.X) / 3, (f.A.Z + f.B.Z + f.C.Z) / 3))
                { feature = f; return MovementAvailability.Known; }
            return MovementAvailability.Unresolved;
        }
        public MovementAvailability GetFeature(MovementSurfaceFeatureKey key, out MovementSurfaceFeature feature)
        {
            feature = default;
            if (key.Source != "fan" || key.Address != 0 || (uint)key.Part >= _features.Length) return MovementAvailability.Unresolved;
            feature = _features[key.Part];
            return MovementAvailability.Known;
        }
        public bool AreAdjacent(MovementSurfaceFeatureKey a, MovementSurfaceFeatureKey b)
        {
            if (GetFeature(a, out var x) != MovementAvailability.Known || GetFeature(b, out var y) != MovementAvailability.Known) return false;
            Vector3[] first = [x.A, x.B, x.C], second = [y.A, y.B, y.C];
            return first.Count(p => second.Contains(p)) >= 2;
        }
    }
}

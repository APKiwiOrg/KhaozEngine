using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Water-policy component proof. Collision certification remains a separate requirement.
public class MovementFreeTopPartitionTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementDomainKey Water = new("world", "water");
    const float Error = 0.00001f;

    [Theory]
    [InlineData(1f, 0.2f, MovementAvailability.Known)]
    [InlineData(1f, -2f, MovementAvailability.Unresolved)]
    [InlineData(-1f, 2f, MovementAvailability.Unresolved)]
    [InlineData(0f, 0f, MovementAvailability.Unresolved)]
    [InlineData(-1f, 0f, MovementAvailability.Unresolved)]
    public void SlopedCeilingPartitionIsCheckedOverTheWholeCoveredFootprint(float startX, float deltaX,
        MovementAvailability expected)
    {
        using var scene = new Scene();
        var body = new MovementBodyQuery(new(startX, -3, 0), 0.25f, 0.75f, Room, null);
        Assert.Equal(expected, Check(body, new(deltaX, 0, 0), scene.Lease));
        if (deltaX != 0 && expected != MovementAvailability.Known)
        {
            Assert.True(scene.SawFree);
            Assert.True(scene.SawClosed);
        }
    }

    [Fact]
    public void ErrorTieIsClosedAndUnplaceablePartitionRefuses()
    {
        using var scene = new Scene();
        float boundary = 2 * Error;
        var body = new MovementBodyQuery(new(boundary + 0.251f, -3, 0), 0.25f, 0.75f, Room, null);
        Assert.Equal(MovementAvailability.Unresolved, Check(body, Vector3.Zero, scene.Lease));
        Assert.True(scene.SawClosed);
        scene.Unplaceable = true;
        body = new(new(1, -3, 0), 0.25f, 0.75f, Room, null);
        Assert.Equal(MovementAvailability.Unresolved, Check(body, Vector3.Zero, scene.Lease));
    }

    delegate MovementAvailability CheckDelegate(in MovementBodyQuery body, Vector3 delta,
        MovementQueryLease lease, WaterTraversalMode mode);
    static MovementAvailability Check(MovementBodyQuery body, Vector3 delta, MovementQueryLease lease)
    {
        var type = typeof(MovementQueryLease).Assembly.GetType("KhaozEngine.Locomotion.MovementWaterPathProof")!;
        var method = type.GetMethod("Check", BindingFlags.Static | BindingFlags.NonPublic)!;
        return method.CreateDelegate<CheckDelegate>()(body, delta, lease, WaterTraversalMode.SurfaceSwimmer);
    }

    sealed class Scene : IDisposable
    {
        readonly BepuPhysicsWorld world = new(Vector3.Zero);
        readonly IPhysicsWorldQueryView view;
        readonly EnvironmentAcquisitionFixture environment;
        public MovementQueryLease Lease { get; }
        public bool Unplaceable;
        public bool SawFree;
        public bool SawClosed;
        public Scene()
        {
            // Actual ceiling y=0.5x. The bounded test body stays fully below it and below water y=0.
            Vector3 normal = Vector3.Normalize(new Vector3(-0.5f, 1, 0));
            world.AddStatic(new BoxShape(new(8, 0.125f, 8)),
                new Pose(normal * 0.125f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan(0.5f))));
            view = world.CreateQueryViewExcludingStatics([]);
            environment = new(view) { OnCoverage = Trace };
            var acquired = environment.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            Lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        }

        MovementCoverageResult Trace(in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts)
        {
            if (Unplaceable || query.Body.Centre.Y != -3 || query.Delta.Y != 0 || query.Delta.Z != 0 ||
                query.Body.Radius != 0.25f || query.Body.HalfHeight != 0.75f ||
                Math.Abs(query.Body.Centre.X) > 2 || Math.Abs(query.Body.Centre.X + query.Delta.X) > 2)
                return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, environment.Identity);
            double origin = query.Body.Centre.X, delta = query.Delta.X;
            double radius = query.Body.Radius + (double)MovementQueryLease.CoverageSkinMetres;
            double boundary = 2d * Error; // 0.5x must exceed surface zero by more than Error.
            var cuts = new SortedSet<float> { 0, 1 };
            if (delta != 0)
            {
                Add((boundary - radius - origin) / delta);
                Add((boundary + radius - origin) / delta);
            }
            var ordered = new float[cuts.Count];
            cuts.CopyTo(ordered);
            var outputSpans = new List<MovementCoverageSpan>();
            var outputContacts = new List<MovementDomainContact>();
            for (int i = 0; i < ordered.Length; i++)
            {
                Append(ordered[i], ordered[i]);
                if (i + 1 < ordered.Length) Append(ordered[i], ordered[i + 1]);
            }
            if (spans.Length < outputSpans.Count || contacts.Length < outputContacts.Count)
                return new(MovementAvailability.CapacityExceeded, 0, outputSpans.Count, 0, outputContacts.Count, Error, environment.Identity);
            outputSpans.ToArray().AsSpan().CopyTo(spans);
            outputContacts.ToArray().AsSpan().CopyTo(contacts);
            return new(MovementAvailability.Known, outputSpans.Count, outputSpans.Count,
                outputContacts.Count, outputContacts.Count, Error, environment.Identity);

            void Add(double t) { if (t > 0 && t < 1) cuts.Add((float)t); }
            void Append(float start, float end)
            {
                double middle = origin + ((double)start + end) * 0.5 * delta;
                int first = outputContacts.Count;
                if (middle - radius <= boundary + Error) Emit(false, start, end);
                if (middle + radius > boundary) Emit(true, start, end);
                outputSpans.Add(new(start, end, first, outputContacts.Count - first, false));
            }
            void Emit(bool free, float at, float end)
            {
                double x = origin + at * delta;
                float column = free ? Math.Max((float)x, MathF.BitIncrement((float)boundary)) : Math.Min((float)x, (float)boundary);
                float upper = free ? 0 : Math.Min(0, 0.5f * column);
                bool overlapping = free ? x + 0.25 >= boundary - Error : x - 0.25 <= boundary + Error;
                double lastX = origin + end * delta;
                bool spanOverlap = free ? Math.Max(x, lastX) + 0.25 >= boundary - Error
                    : Math.Min(x, lastX) - 0.25 <= boundary + Error;
                outputContacts.Add(new(Water, Room, new(-6, upper, 0, free, "floor", free ? "surface" : "ceiling"),
                    new(column, 0), free ? -Vector3.UnitX : Vector3.UnitX, at, "upper-kind", free ? 2u : 1u,
                    overlapping ? MovementContactOverlap.Overlapping : MovementContactOverlap.Tangent,
                    spanOverlap ? MovementContactOverlap.Overlapping : MovementContactOverlap.Tangent));
                SawFree |= free;
                SawClosed |= !free;
            }
        }
        public void Dispose() { Lease.Dispose(); view.Dispose(); world.Dispose(); }
    }
}

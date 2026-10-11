using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactMediumRouteTests
{
    // Exact plane/slab coverage with real Bepu solids. These synthetic region certificates exercise the
    // controller's admission rules, independently of a consumer's water producer.
    sealed class RouteScene : IDisposable
    {
        public readonly BepuPhysicsWorld World = new(Vector3.Zero);
        readonly IPhysicsWorldQueryView view;
        public readonly EnvironmentAcquisitionFixture Environment;
        public readonly MovementQueryLease Lease;
        public float WaterMinX = float.PositiveInfinity;
        public float WaterMaxX = float.PositiveInfinity;
        public float WaterBottom = -5;
        public Vector3? WaterNormal;
        public bool SecondShore, ReverseContacts, ProgressSeen, FailSeat, DenyMappedRetrace;
        readonly HashSet<(Vector3, Vector3)> tangentTraces = [];
        public string LateFault = "";

        public RouteScene(float height = 0, Action<BepuPhysicsWorld>? configure = null, Vector3? normal = null)
        {
            configure?.Invoke(World);
            view = World.CreateQueryViewExcludingStatics([]);
            var identity = new MovementQueryIdentity("closure", 1, "scope");
            var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
            var scope = new MovementQueryScope(new(-20), new(20), 10, 10, Room, identity, frame);
            Environment = new(view, scope) { ContactSupportEnabled = true };
            Environment.Point = new(MovementAvailability.Known, Room, null, false, 1, null);
            Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
            {
                ground = new(new(request.Body.Centre.X, request.Body.Centre.Z), height, normal ?? Vector3.UnitY,
                    new("world", "floor"), Room, null, identity);
                return MovementAvailability.Known;
            };
            Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
            {
                binding = new(new("world", "floor"), Room, null, identity);
                if (LateFault == "binding" && request.Axis.X > 0.15f)
                { ProgressSeen = true; return MovementAvailability.Unresolved; }
                return MovementAvailability.Known;
            };
            Environment.OnCoverage = Coverage;
            var acquired = Environment.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            Lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        }

        public LeasedContactQueryContext Queries(MovementBoundary? boundary = null) => new(Lease, Tuning,
            new(Room, null, Lease.Identity), new(Room, Vector3.Zero), boundary: boundary);

        MovementCoverageResult Coverage(in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts)
        {
            if (DenyMappedRetrace && query.Body.Centre.X > 0.74f && query.Delta.X == 0 && query.Delta.Z > 0 &&
                !tangentTraces.Add((query.Body.Centre, query.Delta)))
            {
                // Adversarial evidence injection. A previously proved physical tangent refuses its mapped
                // retrace. This is not a new geometry certificate or a valid partial-tick obstruction.
                ProgressSeen = true;
                spans[0] = new(0, 1, 0, 1, false);
                contacts[0] = new(new("world", "water"), Room, new(-5, 5, 5, false, "bottom", "top"),
                    new(query.Body.Centre.X, query.Body.Centre.Z), -Vector3.UnitX, 0, "shore-x", 1,
                    MovementContactOverlap.Overlapping, MovementContactOverlap.Overlapping);
                return new(MovementAvailability.Known, 1, 1, 1, 1, 0, Lease.Identity);
            }
            if (FailSeat)
            {
                if (query.Delta.X != 0) ProgressSeen = true;
                if (ProgressSeen && query.Delta.X == 0 && query.Delta.Y != 0)
                    return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, Lease.Identity);
            }
            if (query.Body.Centre.X > 0.15f && query.Delta.X > 0 && LateFault is "coverage" or "stale")
            {
                ProgressSeen = true;
                if (LateFault == "stale") Environment.Fault = "stale-pin";
                return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, Lease.Identity);
            }
            double enter = 0, exit = 1;
            Vector3 normal = -Vector3.UnitX;
            double verticalGap = Math.Max(0, WaterBottom - query.Body.Centre.Y -
                ((double)query.Body.HalfHeight - query.Body.Radius));
            double radial = verticalGap < query.Body.Radius
                ? Math.Sqrt((double)query.Body.Radius * query.Body.Radius - verticalGap * verticalGap) : 0;
            double low = WaterBottom - query.Body.HalfHeight, high = 5 + query.Body.HalfHeight;
            if (query.Delta.X == 0 && WaterMinX != float.PositiveInfinity)
            {
                double dx = Math.Clamp(query.Body.Centre.X, WaterMinX, WaterMaxX) - query.Body.Centre.X;
                double cap = Math.Sqrt(Math.Max(0, (double)query.Body.Radius * query.Body.Radius - dx * dx));
                low = WaterBottom - ((double)query.Body.HalfHeight - query.Body.Radius) - cap;
                high = 5 + ((double)query.Body.HalfHeight - query.Body.Radius) + cap;
                radial = query.Body.Radius;
            }
            bool wet = WaterMinX != float.PositiveInfinity &&
                Clip(query.Body.Centre.Y, query.Delta.Y, low, high,
                    -Vector3.UnitY, ref enter, ref exit, ref normal) &&
                Clip(query.Body.Centre.X, query.Delta.X, WaterMinX - radial, WaterMaxX + radial,
                    -Vector3.UnitX, ref enter, ref exit, ref normal);
            int count = wet ? SecondShore ? 2 : 1 : 0;
            if (!wet)
            {
                spans[0] = new(0, 1, 0, 0, true);
                return new(MovementAvailability.Known, 1, 1, 0, 0, 0, Lease.Identity);
            }
            float a = (float)enter, b = (float)exit;
            normal = WaterNormal ?? normal;
            Vector3 at = query.Body.Centre + query.Delta * a;
            var interval = new MovementWaterInterval(WaterBottom, 5, 5, false, "bottom", "top");
            var column = new Vector2(Math.Clamp(at.X, WaterMinX, WaterMaxX), at.Z);
            contacts[ReverseContacts && SecondShore ? 1 : 0] = new(new("world", "water"), Room, interval,
                column, normal, a, "shore-x", 1, MovementContactOverlap.Overlapping,
                MovementContactOverlap.Overlapping);
            if (SecondShore)
                contacts[ReverseContacts ? 0 : 1] = new(new("world", "water"), Room, interval,
                    new(at.X, 1), -Vector3.UnitZ, a, "shore-z", 2,
                    MovementContactOverlap.Overlapping,
                    MovementContactOverlap.Overlapping);
            int written = 0;
            if (a > 0) spans[written++] = new(0, a, 0, 0, true);
            spans[written++] = new(a, b, 0, count, false);
            if (b < 1) spans[written++] = new(b, 1, count, 0, true);
            return new(MovementAvailability.Known, written, written, count, count, 0, Lease.Identity);
        }

        static bool Clip(double start, double delta, double min, double max, Vector3 inward,
            ref double enter, ref double exit, ref Vector3 normal)
        {
            if (delta == 0) return start >= min && start <= max;
            double a = (min - start) / delta, b = (max - start) / delta;
            Vector3 candidate = delta > 0 ? inward : -inward;
            if (a > b) (a, b) = (b, a);
            if (a > enter) { enter = a; normal = candidate; }
            exit = Math.Min(exit, b);
            return enter <= exit && exit >= 0 && enter <= 1;
        }

        public void Dispose() { Lease.Dispose(); view.Dispose(); World.Dispose(); }
    }
}

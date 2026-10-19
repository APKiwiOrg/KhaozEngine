using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement.Fixtures;

public enum ContactNavigationReuseMode { Off, Footprints, Combined }

internal sealed class ContactNavigationReuseFixture : IDisposable
{
    internal static readonly (int X, int Z)[] Directions =
        [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];
    internal static ContactNavigationBuildHooks Hooks(ContactNavigationReuseMode mode) => new()
    {
        DisableFootprintReuse = mode == ContactNavigationReuseMode.Off,
        DisableInitialHoldReuse = mode != ContactNavigationReuseMode.Combined,
    };

    internal static ContactNavigationBaselineTests.BaselineBake Build(string fixture, ContactNavigationReuseMode mode, int degree)
    {
        using var hooks = ContactNavigationBuildHooks.Install(Hooks(mode));
        return ContactNavigationBaselineTests.BuildFixture(fixture, new NavBakeExecution(degree)
        { UseStableContactHoldReuse = mode == ContactNavigationReuseMode.Combined });
    }

    internal byte[] ProveInOrder(bool reverseSources, bool reverseDirections)
    {
        var baked = GroundNavigationBake.Create(Bake, ContactBakeFile.Sources(), [new("player", Tuning, default)],
            new NavBakeExecution(1) { UseStableContactHoldReuse = true });
        ProveInOrder(Bake, Context, baked, reverseSources, reverseDirections, ContactNavigationReusePolicy.Capture(
            new NavBakeExecution(1) { UseStableContactHoldReuse = true }));
        return ContactBakeFile.Write(baked);
    }

    // Re-evaluate real proofs, then write their verdicts into canonical slots in this locally owned bake.
    internal static void ProveInOrder(PhysicsNavBake capture, GroundMoveContext context, GroundNavigationBake baked,
        bool reverseSources, bool reverseDirections, ContactNavigationReusePolicy policy)
    {
        foreach (string name in baked.ProfileNames)
        {
            GroundNavigation original = baked.GetProfile(name);
            if (original.Identity.Kind != EffectiveMovementBindingKind.OrdinaryContact) continue;
            MoveTuning tuning = original.Identity.Tuning;
            using var source = context.CreateNavigationBinding(tuning);
            using var proof = new ContactNavigationProof(capture, context, source, original.Footprint, policy);
            var cells = new List<(int Layer, int X, int Z)>();
            var nodes = new bool[original.Space.Layers.Count][];
            var exits = new byte[nodes.Length][];
            var addresses = new ContactNavigationSources();
            for (int layer = 0; layer < nodes.Length; layer++)
            {
                NavGrid grid = original.Space.Layers[layer];
                nodes[layer] = new bool[grid.Width * grid.Height];
                exits[layer] = new byte[nodes[layer].Length];
                for (int z = 0; z < grid.Height; z++)
                    for (int x = 0; x < grid.Width; x++)
                    {
                        if (!original.Graph.IsNodePassable(layer, x, z)) continue;
                        nodes[layer][z * grid.Width + x] = true;
                        cells.Add((layer, x, z));
                        float raw = grid.SurfaceHeightAt(x, z)!.Value;
                        Vector2 centre = grid.CellCenter(x, z);
                        var column = capture.Columns.GetColumn(x, z);
                        for (int ordinal = 0; ordinal < column.Length; ordinal++)
                            if (BitConverter.SingleToInt32Bits(column[ordinal].Height) == BitConverter.SingleToInt32Bits(raw))
                                addresses.Record(new(centre.X, raw, centre.Y), new(x, z, ordinal, BitConverter.SingleToInt32Bits(raw)));
                    }
            }
            addresses.BindLayers(original.Space);
            if (reverseSources) cells.Reverse();
            int[] directions = reverseDirections ? [7, 6, 5, 4, 3, 2, 1, 0] : [0, 1, 2, 3, 4, 5, 6, 7];
            foreach (var cell in cells)
            {
                NavGrid grid = original.Space.Layers[cell.Layer];
                Assert.True(addresses.TryGet(cell.Layer, cell.X, cell.Z, out var address));
                using var group = proof.BeginSource(address);
                foreach (int direction in directions)
                {
                    int nx = cell.X + Directions[direction].X, nz = cell.Z + Directions[direction].Z;
                    if (!original.Graph.IsNodePassable(cell.Layer, nx, nz)) continue;
                    if (proof.TryEdge(Feet(grid, cell.X, cell.Z), Feet(grid, nx, nz)))
                        exits[cell.Layer][cell.Z * grid.Width + cell.X] |= (byte)(1 << direction);
                }
            }
            var layers = original.Space.Layers.Select((grid, layer) =>
                new NavTraversalLayer(grid.Width, grid.Height, nodes[layer], exits[layer])).ToArray();
            var links = original.Space.Links.Where(link => link.Kind == NavLinkKind.Stair &&
                original.Graph.IsNodePassable(link.FromLayer, link.FromX, link.FromZ) &&
                original.Graph.IsNodePassable(link.ToLayer, link.ToX, link.ToZ) &&
                proof.TryEdge(Feet(original.Space.Layers[link.FromLayer], link.FromX, link.FromZ),
                    Feet(original.Space.Layers[link.ToLayer], link.ToX, link.ToZ))).ToArray();
            var replacement = new GroundNavigation(new NavTraversalGraph(original.Space, original.AgentRadius,
                original.AgentHeight, layers, links), tuning, original.Footprint, original.Aquatic);
            SameGraphBits(original, replacement);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var profiles = (GroundNavigation[])typeof(GroundNavigationBake).GetField("_profiles", fields)!.GetValue(baked)!;
            profiles[Array.IndexOf(baked.ProfileNames.ToArray(), name)] = replacement;
            var byName = (Dictionary<string, GroundNavigation>)typeof(GroundNavigationBake).GetField("_byName", fields)!.GetValue(baked)!;
            byName[name] = replacement;
            proof.AssertCurrent();
        }

        static Vector3 Feet(NavGrid grid, int x, int z)
        {
            Vector2 centre = grid.CellCenter(x, z);
            return new(centre.X, grid.RestingHeightAt(x, z)!.Value, centre.Y);
        }
    }

    internal readonly ConcurrentQueue<object> Trace = new();
    internal readonly OrdinaryContactScene Scene;
    internal readonly GroundMoveContext Context;
    internal readonly PhysicsNavBake Bake;
    internal readonly Vector3 From, To;
    internal readonly TracedWater Water;
    internal readonly ConcurrentQueue<FramedMovementState> StepInputs = new();
    internal bool FailProvider;
    internal float HeightValue;
    internal Action<float, float>? OnHeight;
    readonly IPhysicsWorldQueryView? _unsupported;

    internal ContactNavigationReuseFixture(bool physicsWins = false, bool unsupported = false, bool skippedFloor = false,
        int maxSteps = 160)
    {
        float top = skippedFloor ? 1 : physicsWins ? 0.125f : 0;
        From = new(-0.075f, top, 0.125f);
        To = From + new Vector3(0.25f, 0, 0);
        Water = new(this);
        var water = Water;
        Scene = new(Tuning, (x, z) =>
        {
            Trace.Enqueue(("height", BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(z)));
            OnHeight?.Invoke(x, z);
            if (FailProvider && x > 0) throw new ProviderFailure();
            return HeightValue;
        }, (x, z) =>
        {
            Trace.Enqueue(("normal", BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(z)));
            return Vector3.UnitY;
        }, water: water, install: (world, builder) =>
        {
            if (skippedFloor) Box(world, builder, "lower", new(-8, -0.25f, -8), new(8, 0, 8));
            Box(world, builder, "floor", new(-8, skippedFloor ? 0.8f : -0.25f, -8), new(8, top, 8));
        });
        _unsupported = unsupported ? new UnsupportedView(Scene.Physics.CreateQueryViewExcludingStatics([])) : null;
        Context = new(Scene.Height, Scene.Normal, Scene.Physics, null, null, _unsupported,
            water, Scene.Registration, MovementBoundary.Rectangle(-20, -20, 20, 20));
        Bake = PhysicsNavBake.Capture(Context, Options with { MaxEdgeProbeSteps = maxSteps }, _ => 0);
        Trace.Clear();
    }

    internal ContactNavigationProof Proof(bool reuse = true, bool holds = false)
    {
        using var source = Context.CreateNavigationBinding(Tuning);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var context = (MovementEnvironmentContext)typeof(EffectiveMovementBinding).GetField("_context", fields)!.GetValue(source)!;
        FieldInfo provider = typeof(MovementEnvironmentContext).GetField("_provider", fields)!;
        provider.SetValue(context, new TracedProvider((IMovementEnvironmentProvider)provider.GetValue(context)!, this));
        return new(Bake, Context, source, new NavAreaFootprint(Bake.Columns, Bake.Options, Tuning, default),
            new ContactNavigationReusePolicy(reuse, holds, false));
    }

    internal static ContactNavigationSource Source(int ordinal = 0, int layer = 0) =>
        new(new(6, 4, ordinal, 0), layer);

    internal static void Released()
    {
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Keys);
        Assert.Equal(0, SupportQueryDiagnostics.Retention.Bytes);
    }

    // Lower the backend's existing internal limit before its first publication. No production test control is added.
    internal static void LimitSession(object session, int capacity)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo field = session.GetType().GetField("Memo", fields)!;
        ((IDisposable)field.GetValue(session)!).Dispose();
        object memo = Activator.CreateInstance(field.FieldType, fields, null, [capacity, 2 * 1024 * 1024], null)!;
        field.SetValue(session, memo);
    }

    internal IEnumerable<(object Session, SupportFootprintReuseDomain Domain)> ActiveSessions()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var sessions = (IEnumerable)Scene.Physics.GetType().GetField("_footprintReuseSessions", fields)!.GetValue(Scene.Physics)!;
        foreach (object pair in sessions)
        {
            object session = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
            if ((int)session.GetType().GetField("_thread", fields)!.GetValue(session)! == Environment.CurrentManagedThreadId)
                yield return (session, (SupportFootprintReuseDomain)session.GetType().GetField("_domain", fields)!.GetValue(session)!);
        }
    }

    internal byte[] Build(NavBakeExecution? execution = null) => ContactBakeFile.Write(GroundNavigationBake.Create(
        Bake, ContactBakeFile.Sources(), [new("player", Tuning, default)], execution));

    internal sealed class ProviderFailure() : Exception("deterministic height failure");
    internal sealed record AnalyticCall(MovementSupportRequest Request, MovementAvailability Status, MovementAnalyticGround? Ground);
    internal sealed record BindingCall(MovementContactSupportRequest Request, MovementAvailability Status, MovementContactSupportBinding Binding);

    public void Dispose()
    {
        Bake.Dispose();
        Context.Dispose();
        _unsupported?.Dispose();
        Scene.Dispose();
    }

    internal sealed class TracedWater(ContactNavigationReuseFixture owner) : IMovementWaterField
    {
        readonly IMovementWaterField _inner = MovementWaterField.Level("world", -3, -4);
        long _generation;
        internal int GenerationReads;
        internal Action? BeforeGenerationRead;
        internal string? IdentityOverride;
        internal MovementWaterExtent? ExtentOverride;
        internal void Advance() => System.Threading.Interlocked.Increment(ref _generation);
        public string SemanticIdentity => IdentityOverride ?? _inner.SemanticIdentity;
        public long Generation
        {
            get
            {
                System.Threading.Interlocked.Increment(ref GenerationReads);
                BeforeGenerationRead?.Invoke();
                return System.Threading.Interlocked.Read(ref _generation);
            }
        }
        public MovementWaterExtent Extent => ExtentOverride ?? _inner.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin)
        {
            owner.Trace.Enqueue(("water", body, origin));
            return _inner.SampleCentreWater(body, origin);
        }
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        {
            owner.Trace.Enqueue(("coverage", query, origin));
            return _inner.TraceWater(query, origin, identity, spans, contacts);
        }
    }

    // Observe real ordinary-producer calls and results. The original provider still owns every side effect.
    sealed class TracedProvider(IMovementEnvironmentProvider inner, ContactNavigationReuseFixture owner) : IMovementEnvironmentProvider
    {
        public string WorldId => inner.WorldId;
        public MovementPreparationResult Prepare(in MovementQueryScope scope) => inner.Prepare(scope);
        public MovementAvailability TryPinPrepared(in MovementQueryScope scope, IPhysicsQueryLease physics,
            out IMovementEnvironmentPin? pin)
        {
            var status = inner.TryPinPrepared(scope, physics, out var original);
            pin = original is null ? null : new TracedPin(original, owner);
            return status;
        }
    }

    sealed class TracedPin(IMovementEnvironmentPin inner, ContactNavigationReuseFixture owner) :
        IMovementEnvironmentPin, IMovementContactSupportPin
    {
        public IPhysicsWorldQueryView PhysicsView => inner.PhysicsView;
        public long GeometryGeneration => inner.GeometryGeneration;
        public long EnvironmentGeneration => inner.EnvironmentGeneration;
        public MovementFrameDescriptor Frame => inner.Frame;
        public MovementScopeWitness Witness => inner.Witness;
        public void AssertCurrent() => inner.AssertCurrent();
        public void Dispose() => inner.Dispose();
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body) => inner.SampleCentreWater(body);
        public MovementSupportSet EnumerateSupport(in MovementSupportRequest request, Span<MovementSupportCandidate> candidates) =>
            inner.EnumerateSupport(request, candidates);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) => inner.TraceWater(query, spans, contacts);
        public MovementAvailability RebuildSelection(in FramedMovementState state, out MovementSelection selection)
        {
            owner.StepInputs.Enqueue(state);
            return inner.RebuildSelection(state, out selection);
        }
        public MovementAvailability SampleAnalyticGround(in MovementSupportRequest request, out MovementAnalyticGround? ground)
        {
            var status = ((IMovementContactSupportPin)inner).SampleAnalyticGround(request, out ground);
            owner.Trace.Enqueue(new AnalyticCall(request, status, ground));
            return status;
        }
        public MovementAvailability BindContactSupport(in MovementContactSupportRequest request, out MovementContactSupportBinding binding)
        {
            var status = ((IMovementContactSupportPin)inner).BindContactSupport(request, out binding);
            owner.Trace.Enqueue(new BindingCall(request, status, binding));
            return status;
        }
    }

    sealed class UnsupportedView(IPhysicsWorldQueryView inner) : PhysicsForwarder(inner), IPhysicsWorldQueryView,
        IPhysicsQueryLeaseSource, IPhysicsSupportFootprint, IPhysicsCapsuleSweep, IPhysicsCapsuleContacts
    {
        public IPhysicsWorld SourceWorld => inner.SourceWorld;
        public IPhysicsQueryLease AcquireQueryReadLease() => ((IPhysicsQueryLeaseSource)inner).AcquireQueryReadLease();
        public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float separation,
            Span<CapsuleContact> destination, QueryFilter filter = default) =>
            ((IPhysicsCapsuleContacts)inner).QueryCapsuleContacts(capsule, pose, separation, destination, filter);
        public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement,
            QueryFilter filter = default) => ((IPhysicsCapsuleSweep)inner).SweepCapsuleCertified(capsule, pose, displacement, filter);
        public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request,
            Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default)
        {
            var result = ((IPhysicsSupportFootprint)inner).QuerySupportFootprint(lease, request, members, joins, filter);
            if (result.Status != CapsuleFeatureStatus.Complete) return result;
            ((IPhysicsSupportFootprint)inner).AssertFootprintCurrent(result, lease);
            return SupportFootprintResult.Completed(this, lease, result.Members, result.Witnesses, result.AxisVerdict, result.FootVerdict);
        }
        public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease)
        {
            lease.AssertCurrent();
            if (!ReferenceEquals(result.QueryWorld, this) || !ReferenceEquals(result.Lease, lease))
                throw new InvalidOperationException("The footprint receiver or lease changed.");
            var original = SupportFootprintResult.Completed(inner, lease, result.Members, result.Witnesses,
                result.AxisVerdict, result.FootVerdict);
            ((IPhysicsSupportFootprint)inner).AssertFootprintCurrent(original, lease);
        }
    }
}

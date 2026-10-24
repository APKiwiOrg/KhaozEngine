using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Movement;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using static KhaozEngine.Tests.Movement.TileWorldMovementNavigationTests;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class FilteredTileNavigationTests
{
    const string WorldId = "filtered-water-bed";
    static readonly MoveTuning Contact = new(2, 5, 0.75f, MathF.PI / 4,
        CapsuleRadius: 0.2f, StepHeight: 0.4f, TractionHysteresisRadians: 0)
    { Controller = MovementControllerKind.Contact, Water = new(WaterTraversalMode.WadeOnly) };
    static readonly MoveTuning Legacy = Contact with { Controller = MovementControllerKind.Legacy, Water = default };
    static readonly PhysicsNavBakeOptions CaptureOptions = new(1, -5, 7, -2, 0.5f, 4, 6,
        Contact.MaxSlopeRadians, 128, 512, MaxEdgeProbeSteps: 160);
    static readonly NavBakeProfile[] ContactProfiles = [new("wader", Contact, default)];
    static readonly NavBakeProfile[] LegacyProfiles = [new("legacy", Legacy, default)];
    static readonly Vector3 WetColumn = new(2.5f, -0.5f, -3.5f);
    static readonly Vector3 DryColumn = new(5.5f, -0.5f, -3.5f);

    [Fact]
    public void FilteredCaptureMatchesInstalledStaticsRatherThanMovementExclusions()
    {
        var fixture = new WaterBed();
        Vector3 origin = new(8, 1, -8);
        using var filtered = new Installation(fixture, fixture.Include, origin);
        using var reference = new Installation(fixture, null, origin, manual: true);
        using var allInstalled = new Installation(fixture, null, origin);
        Assert.Equal(TileColliderKind.Blocked, fixture.Colliders.Colliders[4].Kind);
        Assert.Equal(new Vector3(2.5f, 1, -3.5f), fixture.Colliders.Colliders[4].Pose.Position);
        Assert.DoesNotContain(4, filtered.Installed!.ColliderIndices);
        Assert.Equal(reference.Registration.SemanticDigest, filtered.Registration.SemanticDigest);

        RayHit bed = DownwardHit(filtered.World, WetColumn, origin);
        Assert.Equal(-0.5f, bed.Point.Y + origin.Y, 0.001f);
        Assert.Contains(bed.Body!.Value, filtered.Installed.GroundHandles);
        Assert.Equal(2.5f, DownwardHit(filtered.World, DryColumn, origin).Point.Y + origin.Y, 0.001f);
        Assert.Equal(2.5f, DownwardHit(allInstalled.World, WetColumn, origin).Point.Y + origin.Y, 0.001f);
        Assert.Equal(filtered.Installed.GroundHandles, filtered.Registration.ExcludedStatics);
        Assert.Same(filtered.World, filtered.Context.MovementQueries!.SourceWorld);
        Assert.False(filtered.Context.MovementQueries.Raycast(WetColumn + Vector3.UnitY * 4 - origin,
            -Vector3.UnitY, 6, out _));
        Assert.Equal(2.5f, DownwardHit(filtered.Context.MovementQueries, DryColumn, origin).Point.Y + origin.Y, 0.001f);

        byte[] filteredBytes = BakeBytes(filtered, LegacyProfiles);
        byte[] referenceBytes = BakeBytes(reference, LegacyProfiles);
        byte[] allInstalledBytes = BakeBytes(allInstalled, LegacyProfiles);
        Assert.Equal(referenceBytes, filteredBytes);
        Assert.False(allInstalledBytes.SequenceEqual(filteredBytes));
    }

    [Fact]
    public void FilteredWaterBedSupportsOrdinaryWadingNavigation()
    {
        var fixture = new WaterBed();
        using var scene = new Installation(fixture, fixture.Include);
        Assert.True(scene.Context.SupportsContactController);
        Assert.Same(scene.World, scene.Registration.SourceWorld);
        Assert.True(fixture.Colliders.Medium.MediumAt(WetColumn.X, WetColumn.Z, WetColumn.Y).InWater);
        using var capture = PhysicsNavBake.Capture(scene.Context, CaptureOptions, scene.Classify);
        GroundNavigation nav = GroundNavigationBake.Create(capture, Sources(fixture), ContactProfiles).GetProfile("wader");
        Vector3 from = new(1.75f, -0.5f, -3.25f), to = new(3.25f, -0.5f, -3.25f);
        NavPath waterBedRoute = Route(nav, from, to);
        Assert.Equal(NavPathStatus.Complete, waterBedRoute.Status);
        Assert.NotEmpty(waterBedRoute.Waypoints);
        Assert.All(waterBedRoute.Waypoints, point =>
        {
            NavGrid grid = nav.Space.Layers[point.Layer];
            (int x, int z) = grid.CellOf(point.Position.X, point.Position.Y);
            Assert.Equal(-0.5f, Assert.IsType<float>(grid.SurfaceHeightAt(x, z)), 0.001f);
            Assert.Equal(-0.5f, Assert.IsType<float>(grid.RestingHeightAt(x, z)), 0.001f);
        });
        Assert.True(nav.AllowsSegment(from, to));
        Vector3 west = new(4.25f, -0.5f, -3.25f), east = new(6.75f, -0.5f, -3.25f);
        Assert.Equal(NavPathStatus.Complete, Route(nav, west, west).Status);
        Assert.Equal(NavPathStatus.Complete, Route(nav, east, east).Status);
        Assert.False(nav.AllowsSegment(west, east));
        NavPath obstacleRoute = Route(nav, west, east);
        Assert.NotEqual(NavPathStatus.Complete, obstacleRoute.Status);
        Assert.All(obstacleRoute.Waypoints, point => Assert.True(point.Position.X < 5 - Contact.CapsuleRadius));
    }

    [Fact]
    public void FilteringPhysicalGroundDoesNotRewriteAnalyticCoverage()
    {
        var fixture = new WaterBed();
        using var scene = new Installation(fixture,
            collider => collider.Kind != TileColliderKind.Ground && fixture.Include(collider));
        Assert.Empty(scene.Installed!.GroundHandles);
        Assert.Empty(scene.Registration.ExcludedStatics);
        Assert.Equal(fixture.Colliders.MovementExtent, scene.Registration.Extent);
        Assert.False(scene.World.Raycast(WetColumn + Vector3.UnitY * 4, -Vector3.UnitY, 6, out _));
        using var capture = PhysicsNavBake.Capture(scene.Context, CaptureOptions, scene.Classify);
        GroundNavigation nav = GroundNavigationBake.Create(capture, Sources(fixture), LegacyProfiles).GetProfile("legacy");
        foreach (NavGrid grid in nav.Space.Layers)
        {
            (int x, int z) = grid.CellOf(WetColumn.X, WetColumn.Z);
            Assert.True(grid.InBounds(x, z));
            Assert.Null(grid.SurfaceHeightAt(x, z));
        }
        Assert.False(nav.AllowsSegment(WetColumn, WetColumn));
        Assert.Equal(-0.5f, fixture.Colliders.MovementGroundHeight(WetColumn.X, WetColumn.Z));
        Assert.Equal(Vector3.UnitY, fixture.Colliders.MovementGroundNormal(WetColumn.X, WetColumn.Z));
        using var binding = EffectiveMovementBinding.Ordinary(Contact, scene.World, scene.Registration,
            fixture.Colliders.MovementGroundHeight, fixture.Colliders.MovementGroundNormal,
            scene.Context.MovementBoundary!, fixture.Colliders.Medium);
        var frame = new MovementFrameDescriptor(default, Vector3.Zero, 1);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var queries));
        using var lease = Assert.IsType<MovementQueryLease>(queries);
        Vector3 centre = WetColumn + Vector3.UnitY * Contact.CapsuleHalfHeight;
        var state = new FramedMovementState(new MoveState { Position = centre, Grounded = true }, frame, null);
        var supported = binding.StepTowards(state, Vector2.Zero, false, 1f / 30, lease);
        Assert.Equal(MovementStepOutcome.Advanced, supported.Outcome);
        Assert.True(supported.State.State.Grounded);
        Assert.Equal(centre, supported.State.State.Position);
    }

    [Fact]
    public void PolicySourceDigestChangeRefusesPersistedContactBake()
    {
        var fixture = new WaterBed();
        using var omitted = new Installation(fixture, fixture.Include);
        using var retained = new Installation(fixture, null);
        NavBakeSources omittedSources = Sources(fixture).AddHashOf("policy/colliders",
            "water-material-policy-v1\nunderlay=3\nomit-blocked-over-water=true\n"u8);
        NavBakeSources retainedSources = Sources(fixture).AddHashOf("policy/colliders",
            "water-material-policy-v1\nunderlay=3\nomit-blocked-over-water=false\n"u8);
        Assert.Equal(omittedSources.Labels, retainedSources.Labels);
        var a = omittedSources.Snapshot();
        var b = retainedSources.Snapshot();
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Label, b[i].Label);
            Assert.Equal(a[i].Label == "policy/colliders", !a[i].Digest.SequenceEqual(b[i].Digest));
        }
        byte[] omittedBytes = BakeBytes(omitted, ContactProfiles, omittedSources);
        byte[] retainedBytes = BakeBytes(retained, ContactProfiles, retainedSources);
        var omittedExpectation = new NavBakeExpectation(CaptureOptions, omittedSources, ContactProfiles);
        var retainedExpectation = omittedExpectation with { Sources = retainedSources };
        AssertLoad(omittedBytes, omittedExpectation, NavBakeLoadStatus.Loaded);
        AssertLoad(retainedBytes, retainedExpectation, NavBakeLoadStatus.Loaded);
        AssertLoad(omittedBytes, omittedExpectation with { Sources = retainedSources }, NavBakeLoadStatus.SourcesChanged);
        AssertLoad(retainedBytes, retainedExpectation with { Sources = omittedSources }, NavBakeLoadStatus.SourcesChanged);
    }

    [Fact]
    public void AllTrueCaptureMatchesDefaultAndHistoricalBytes()
    {
        byte[] defaultNavigationBytes = HistoricalNavigationFile(includeAll: false);
        byte[] allTrueNavigationBytes = HistoricalNavigationFile(includeAll: true);
        using Stream fixture = typeof(TileInstallationCompatibilityTests).Assembly.GetManifestResourceStream(
            "KhaozEngine.Tests.Fixtures.TileInstallationBaseline.navbake")
            ?? throw new InvalidOperationException("The frozen navigation fixture is missing.");
        using var copy = new MemoryStream();
        fixture.CopyTo(copy);
        byte[] frozenNavigationBytes = copy.ToArray();
        Assert.Equal(frozenNavigationBytes, TileInstallationCompatibilityTests.NormalizeNavigationFile(defaultNavigationBytes));
        Assert.Equal(frozenNavigationBytes, TileInstallationCompatibilityTests.NormalizeNavigationFile(allTrueNavigationBytes));
        Assert.Equal(defaultNavigationBytes, allTrueNavigationBytes);
    }

    static RayHit DownwardHit(IPhysicsWorld queries, Vector3 feet, Vector3 origin)
    {
        Assert.True(queries.Raycast(feet + Vector3.UnitY * 4 - origin, -Vector3.UnitY, 6, out var hit));
        return hit;
    }

    static NavPath Route(GroundNavigation nav, Vector3 from, Vector3 to) =>
        nav.Planner.FindPath(from, to, nav.AgentRadius, PathQueryBudget.Default);

    static NavBakeSources Sources(WaterBed fixture) => new NavBakeSources()
        .Add("colliders", fixture.Colliders.Hash).AddHashOf("catalog", "water-underlay=3\nblocked-height=3\n"u8);

    static byte[] BakeBytes(Installation scene, NavBakeProfile[] profiles, NavBakeSources? sources = null)
    {
        using var capture = PhysicsNavBake.Capture(scene.Context, CaptureOptions, scene.Classify);
        return Write(GroundNavigationBake.Create(capture, sources ?? Sources(scene.Fixture), profiles));
    }

    static byte[] Write(GroundNavigationBake bake)
    {
        using var stream = new MemoryStream();
        bake.WriteTo(stream);
        return stream.ToArray();
    }

    static void AssertLoad(byte[] bytes, NavBakeExpectation expected, NavBakeLoadStatus status)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        NavBakeLoadResult result = GroundNavigationBake.Load(stream, expected);
        Assert.Equal(status, result.Status);
        if (status == NavBakeLoadStatus.Loaded)
            Assert.Equal(bytes, Write(Assert.IsType<GroundNavigationBake>(result.Bake)));
        else
        {
            Assert.Contains("policy/colliders", result.Detail, StringComparison.Ordinal);
            Assert.Null(result.Bake);
        }
    }

    static byte[] HistoricalNavigationFile(bool includeAll)
    {
        using var scene = new Scene(TileWorldNavigationBakeTests.DeckDoorWorld(), TileWorldNavigationBakeTests.Origin,
            include: includeAll ? _ => true : null);
        using var capture = scene.Capture(TileWorldNavigationBakeTests.Options,
            feet => scene.Colliders.Medium.MediumAt(feet.X, feet.Z, feet.Y).InWater ? Wet : Dry);
        return Write(GroundNavigationBake.Create(capture, new NavBakeSources().Add("colliders", scene.Colliders.Hash),
            TileWorldNavigationBakeTests.Profiles));
    }

    sealed class WaterBed
    {
        readonly TileWorldDocument _document = TileWorldPhysicsTestData.FlatWorld();
        readonly TileWorldCatalogs _catalogs = TileWorldPhysicsTestData.Catalogs();
        internal TileWorldColliders Colliders { get; }

        internal WaterBed()
        {
            for (int z = 0; z < TileRegion.Size; z++)
                for (int x = 0; x < TileRegion.Size; x++)
                {
                    _document.SetCornerHeightCm(x, z, 0, -50);
                    if (x < 4) _document.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
                }
            // A distant rim establishes shallow water without sloping the bounded capture's bed.
            _document.SetCornerHeightCm(1, 0, 0, 0);
            _document.SetSettings(2, 3, 0, TileSettings.Blocked);
            for (int z = 0; z < TileRegion.Size; z++) _document.SetSettings(5, z, 0, TileSettings.Blocked);
            Colliders = TileWorldColliders.Build(_document, _catalogs, WorldId, new() { BlockedHeight = 3 });
        }

        internal bool Include(TileCollider collider)
        {
            if (collider.Kind != TileColliderKind.Blocked) return true;
            int x = (int)MathF.Floor(TileWorldSpace.TileX(collider.Pose.Position.X, _document.TileSize));
            int z = (int)MathF.Floor(TileWorldSpace.TileZ(collider.Pose.Position.Z, _document.TileSize));
            return _catalogs.Material(_document.GetUnderlay(x, z, 0))?.Kind != GroundMaterialKind.Water;
        }
    }

    sealed class Installation : IDisposable
    {
        readonly List<StaticHandle> _manualHandles = [];
        internal WaterBed Fixture { get; }
        internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
        internal TileColliderRegistration? Installed { get; }
        internal OrdinaryMovementRegistration Registration { get; }
        internal GroundMoveContext Context { get; }

        internal Installation(WaterBed fixture, Func<TileCollider, bool>? include,
            Vector3 origin = default, bool manual = false)
        {
            Fixture = fixture;
            if (origin != Vector3.Zero) World.Rebase(origin);
            TileWorldColliders colliders = fixture.Colliders;
            if (manual)
            {
                var builder = OrdinaryMovementRegistration.CreateBuilder(WorldId, World, colliders.MovementExtent);
                Vector3 installationOrigin = World.Origin;
                for (int index = 0; index < colliders.Colliders.Count; index++)
                {
                    // The canonical fixture's only omitted row is the water box at (2, 3).
                    if (index == 4) continue;
                    TileCollider collider = colliders.Colliders[index];
                    StaticHandle handle = World.AddStatic(collider.Shape,
                        collider.Pose with { Position = collider.Pose.Position - installationOrigin });
                    _manualHandles.Add(handle);
                    string owner = collider.Kind == TileColliderKind.Ground ? "ground/0/0/0"
                        : FormattableString.Invariant($"blocked/0/5/{(int)-collider.Pose.Position.Z}");
                    var row = TileColliderProvenance.Capture(owner, collider);
                    builder.Add(handle, 0, int.MaxValue, new(WorldId, owner), row.AbsoluteMinimum, row.AbsoluteMaximum);
                    if (collider.Kind == TileColliderKind.Ground) builder.Exclude(handle);
                }
                using var lease = World.AcquireQueryReadLease();
                Registration = builder.Build(lease);
            }
            else
            {
                Installed = include is null ? colliders.AddTo(World) : colliders.AddTo(World, include);
                Registration = Installed.CreateMovementRegistration(WorldId);
            }
            Context = new(colliders.MovementGroundHeight, colliders.MovementGroundNormal, World, null,
                colliders.Medium.MediumDelegate, null, colliders.Medium, Registration,
                MovementBoundary.Rectangle(-origin.X, -64 - origin.Z, 64 - origin.X, -origin.Z));
        }

        internal uint Classify(Vector3 feet) => Fixture.Colliders.Medium.MediumAt(feet.X, feet.Z, feet.Y).InWater ? Wet : Dry;

        public void Dispose()
        {
            Context.Dispose();
            Installed?.Dispose();
            foreach (StaticHandle handle in _manualHandles) World.RemoveStatic(handle);
            World.Dispose();
        }
    }
}

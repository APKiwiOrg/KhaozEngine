using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using static KhaozEngine.Tests.TileWorld.Physics.TileWorldPhysicsTestData;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class GroundMeshQueryOwnershipTests
{
    const float Dt = 1f / 30f;
    static readonly MoveTuning Character = new(WalkSpeed: 2f, RunSpeed: 5f,
        CapsuleHalfHeight: 0.75f, MaxSlopeRadians: MathF.PI / 4f, CapsuleRadius: 0.3f, StepHeight: 0.4f);
    static readonly MoveCommand East = new(new Vector2(0f, 1f), run: false, cameraYaw: -MathF.PI / 2f);
    static readonly MoveCommand Idle = new(Vector2.Zero, run: false, cameraYaw: -MathF.PI / 2f);

    [Fact]
    public void MovementQueriesExcludeOnlyRegisteredGround()
    {
        // Catches selection that removes solids, leaves terrain queryable or mutates the complete owner.
        using var fullWorld = new BepuPhysicsWorld();
        TileWorldColliders colliders = TileWorldColliders.Build(EveryKindWorld(), Catalogs());
        using TileColliderRegistration registration = colliders.AddTo(fullWorld);
        using IPhysicsWorldQueryView selected = registration.CreateMovementQueryView();
        Assert.Same(fullWorld, selected.SourceWorld);
        Vector3 column = new(30.5f, 6.05f, -30.5f);
        Assert.True(fullWorld.Raycast(column, -Vector3.UnitY, 4f, out RayHit ground));
        Assert.Equal(3.05f, ground.Point.Y, 0.001f);
        Assert.Contains(ground.Body!.Value, registration.GroundHandles);
        Assert.False(selected.Raycast(column, -Vector3.UnitY, 4f, out _));
        var capsule = new CapsuleShape(0.3f, 0.9f);
        Assert.True(fullWorld.SweepCapsule(capsule, Pose.At(column), -Vector3.UnitY, 4f, out _));
        Assert.False(selected.SweepCapsule(capsule, Pose.At(column), -Vector3.UnitY, 4f, out _));
        Pose overlapping = Pose.At(new Vector3(column.X, 3.75f, column.Z));
        Assert.True(fullWorld.ComputePenetration(capsule, overlapping, out _));
        Assert.False(selected.ComputePenetration(capsule, overlapping, out _));

        var kinds = new HashSet<TileColliderKind>();
        for (int index = 0; index < colliders.Colliders.Count; index++)
        {
            TileCollider collider = colliders.Colliders[index];
            if (collider.Kind == TileColliderKind.Ground) continue;
            BoxShape box = Assert.IsType<BoxShape>(collider.Shape);
            Vector3 above = collider.Pose.Position + Vector3.UnitY * (box.HalfExtents.Y + 2f);
            Assert.True(selected.Raycast(above, -Vector3.UnitY, 3f, out RayHit solid), $"lost {collider.Kind}");
            Assert.Equal(registration.Handles[index], solid.Body);
            kinds.Add(collider.Kind);
        }
        Assert.Equal(new[] { TileColliderKind.Wall, TileColliderKind.Blocked, TileColliderKind.Object,
            TileColliderKind.WalkSurface }, kinds.OrderBy(kind => kind));
        StaticHandle later = fullWorld.AddStatic(new BoxShape(Vector3.One), Pose.At(new Vector3(35f, 6f, -35f)));
        Assert.True(selected.Raycast(new Vector3(35f, 9f, -35f), -Vector3.UnitY, 4f, out RayHit added));
        Assert.Equal(later, added.Body);
        selected.Dispose();
        Assert.True(fullWorld.Raycast(column, -Vector3.UnitY, 4f, out RayHit retainedGround));
        Assert.Equal(ground.Body, retainedGround.Body);
    }

    [Fact]
    public void GroundHandleSubsetMatchesCanonicalKinds()
    {
        // Catches guessed handle ranges, reordered metadata and metadata lost on removal.
        using var fullWorld = new BepuPhysicsWorld();
        TileWorldColliders colliders = TileWorldColliders.Build(MultiRegionWorld(new RegionCoord(0, 0),
            new RegionCoord(1, 0), new RegionCoord(0, -1), new RegionCoord(-1, 1)), Catalogs());
        using TileColliderRegistration registration = colliders.AddTo(fullWorld);
        StaticHandle[] expected = colliders.Colliders.Select((collider, index) => (collider, index))
            .Where(pair => pair.collider.Kind == TileColliderKind.Ground)
            .Select(pair => registration.Handles[pair.index]).ToArray();
        Assert.Equal(4, expected.Length);
        Assert.Equal(expected, registration.GroundHandles);
        IReadOnlyList<StaticHandle> cached = registration.GroundHandles;
        Assert.Same(cached, registration.GroundHandles);
        Assert.True(Assert.IsAssignableFrom<ICollection<StaticHandle>>(cached).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ((ICollection<StaticHandle>)cached).Clear());
        registration.Remove();
        Assert.Same(cached, registration.GroundHandles);
        Assert.Equal(expected, registration.GroundHandles);
        Assert.Equal(colliders.Colliders.Count, registration.Handles.Count);
        Assert.Throws<ArgumentException>(() => registration.CreateMovementQueryView());
    }

    [Fact]
    public void MultipleRegistrationsUseUnionExclusion()
    {
        // Catches an exclusion snapshot hiding unrelated registrations or a union missing either ground subset.
        using var fullWorld = new BepuPhysicsWorld();
        TileWorldDocument raised = FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++) raised.SetCornerHeightCm(x, z, 0, 200);
        using TileColliderRegistration lower = TileWorldColliders.Build(FlatWorld(), Catalogs()).AddTo(fullWorld);
        using TileColliderRegistration upper = TileWorldColliders.Build(raised, Catalogs()).AddTo(fullWorld);
        Assert.Single(lower.GroundHandles);
        Assert.Single(upper.GroundHandles);
        Vector3 above = new(30.5f, 5f, -30.5f);
        using IPhysicsWorldQueryView withoutLower = lower.CreateMovementQueryView();
        Assert.True(withoutLower.Raycast(above, -Vector3.UnitY, 6f, out RayHit upperHit));
        Assert.Equal(2f, upperHit.Point.Y, 0.001f);
        Assert.Equal(upper.GroundHandles[0], upperHit.Body);
        using IPhysicsWorldQueryView withoutUpper = upper.CreateMovementQueryView();
        Assert.True(withoutUpper.Raycast(above, -Vector3.UnitY, 6f, out RayHit lowerHit));
        Assert.Equal(0f, lowerHit.Point.Y, 0.001f);
        Assert.Equal(lower.GroundHandles[0], lowerHit.Body);
        StaticHandle[] union = lower.GroundHandles.Concat(upper.GroundHandles).ToArray();
        using IPhysicsWorldQueryView withoutBoth = fullWorld.CreateQueryViewExcludingStatics(union);
        Assert.Same(fullWorld, withoutBoth.SourceWorld);
        Assert.False(withoutBoth.Raycast(above, -Vector3.UnitY, 6f, out _));
        Assert.True(fullWorld.Raycast(above, -Vector3.UnitY, 6f, out RayHit complete));
        Assert.Equal(upperHit.Body, complete.Body);
    }

    [Fact]
    public void CompleteCaptureAndDryProofRetainSelectedMovement()
    {
        // Catches filtered capture, analytic fallback and dry proofs losing selected terrain ownership.
        TileWorldDocument document = Ramp(95);
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                if (x is < 10 or >= 12 || z is < 30 or >= 32)
                    document.SetSettings(x, z, 0, TileSettings.NoDraw);
        TileWorldColliders colliders = TileWorldColliders.Build(document, Catalogs());
        using var fullWorld = new BepuPhysicsWorld();
        using TileColliderRegistration registration = colliders.AddTo(fullWorld);
        using IPhysicsWorldQueryView selected = registration.CreateMovementQueryView();
        Assert.Same(fullWorld, selected.SourceWorld);
        Vector3 above = new(10.5f, 15f, -30.5f);
        Assert.True(fullWorld.Raycast(above, -Vector3.UnitY, 10f, out RayHit ground));
        Assert.Equal(9.975f, ground.Point.Y, 0.001f);
        Assert.Contains(ground.Body!.Value, registration.GroundHandles);
        Assert.False(selected.Raycast(above, -Vector3.UnitY, 10f, out _));
        bool capturing = true;
        int heightCalls = 0, mediumCalls = 0;
        var context = new GroundMoveContext((x, z) =>
        {
            if (capturing) throw new InvalidOperationException("Capture must query complete physics ground.");
            heightCalls++;
            return colliders.Ground.HeightAt(x, z);
        }, colliders.Ground.NormalDelegate, physics: fullWorld, clampXz: null,
            medium: (_, _, _) =>
            {
                mediumCalls++;
                return new MovementMedium(20f, inWater: true, wadeSpeedScale: 0.1f);
            }, movementQueries: selected);
        var options = new PhysicsNavBakeOptions(10f, -32f, 12f, -30f, 0.25f,
            15f, 10f, Character.MaxSlopeRadians, 128, 512);
        using PhysicsNavBake bake = PhysicsNavBake.Capture(context, options, _ => 1u);
        Assert.Same(fullWorld, context.Physics);
        Assert.Same(selected, context.MovementQueries);
        Assert.Equal(0, heightCalls);
        Assert.Equal(0, mediumCalls);
        Assert.Equal(64, bake.Columns.SurfaceCount);
        ReadOnlySpan<float> heights = [9.61875f, 9.85625f, 10.09375f, 10.33125f,
            10.56875f, 10.80625f, 11.04375f, 11.28125f];
        for (int z = 0; z < 8; z++)
            for (int x = 0; x < 8; x++)
            {
                ReadOnlySpan<PhysicsNavSurface> column = bake.Columns.GetColumn(x, z);
                Assert.Equal(1, column.Length);
                Assert.Equal(heights[x], column[0].Height, 0.001f);
                Assert.Equal(1u, column[0].Areas);
            }
        capturing = false;
        GroundNavigation nav = bake.BuildProfile(Character, default);
        Vector3 from = new(10.625f, 10.09375f, -31.125f), to = new(11.375f, 10.80625f, -31.125f);
        Assert.True(nav.Graph.CanTraverse(0, 2, 3, 0, 3, 3));
        Assert.True(nav.Graph.CanTraverse(0, 3, 3, 0, 2, 3));
        foreach ((Vector3 start, Vector3 goal) in new[] { (from, to), (to, from) })
        {
            NavPath path = nav.Planner.FindPath(start, goal, nav.AgentRadius, PathQueryBudget.Default);
            Assert.Equal(NavPathStatus.Complete, path.Status);
            Assert.NotEmpty(path.Waypoints);
            Vector3 previous = start;
            foreach (NavWaypoint waypoint in path.Waypoints)
            {
                Assert.Equal(NavWaypointKind.Walk, waypoint.Kind);
                NavGrid grid = nav.Space.Layers[waypoint.Layer];
                (int x, int z) = grid.CellOf(waypoint.Position.X, waypoint.Position.Y);
                Vector3 next = new(waypoint.Position.X, grid.SurfaceHeightAt(x, z)!.Value, waypoint.Position.Y);
                Assert.True(nav.AllowsSegment(previous, next));
                previous = next;
            }
            Assert.True(nav.AllowsSegment(previous, goal));
        }
        Assert.True(heightCalls > 0);
        Assert.Equal(0, mediumCalls);
        Assert.True(fullWorld.Raycast(above, -Vector3.UnitY, 10f, out RayHit retained));
        Assert.Equal(ground.Body, retained.Body);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void IdleOnRegisteredWalkableSlopeDoesNotDrift(int walkingTicks)
    {
        // Catches an idle terrain overlap and a stale correction after releasing uphill input.
        using var scene = new Scene(Ramp(95));
        scene.AssertCompleteGround(10.25f, -30.5f, 9.7375f);
        MoveState state = scene.Place(10.5f, -30.5f);
        for (int tick = 0; tick < walkingTicks; tick++) state = scene.Step(state, East);
        Vector3 released = state.Position;
        if (walkingTicks > 0) Assert.Equal(2f, released.X - 10.5f, 0.02f);
        for (int tick = 0; tick < 120; tick++)
        {
            state = scene.Step(state, Idle);
            AssertTerrainSeat(scene, state);
            Assert.True(Vector2.Distance(new Vector2(released.X, released.Z),
                new Vector2(state.Position.X, state.Position.Z)) < 0.001f, $"idle drift at tick {tick}");
        }
        scene.AssertCompleteGround(10.25f, -30.5f, 9.7375f);
    }

    [Fact]
    public void RegisteredSteepSlopeMatchesAnalyticSlide()
    {
        // Catches filtered terrain granting traction beyond the analytic retained-traction ceiling.
        using var scene = new Scene(Ramp(115));
        using var control = new Scene(scene.Colliders, includeGround: false);
        Assert.True(MathF.Atan(1.15f) > Character.MaxSlopeRadians + Character.TractionHysteresisRadians);
        scene.AssertCompleteGround(30.5f, -30.5f, 35.075f);
        MoveState selected = scene.Place(30.5f, -30.5f), omitted = selected;
        for (int tick = 0; tick < 60; tick++)
        {
            selected = scene.Step(selected, East);
            omitted = control.Step(omitted, East);
            AssertFinite(selected);
            AssertEquivalentStates(omitted, selected);
            Assert.False(selected.Grounded, $"steep terrain granted stable footing at tick {tick}");
            Assert.False(selected.SupportGranted, $"steep terrain granted support at tick {tick}");
        }
        Assert.True(selected.Position.X < 30.5f, "the steep plane did not slide downhill");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredFlatSolidsStopWalking(bool blockedTile)
    {
        // Catches exclusion that drops a wall or blocked tile together with the ground.
        TileWorldDocument document = FlatWorld();
        if (blockedTile) document.SetSettings(15, 30, 0, TileSettings.Blocked);
        else document.AddObject("wall", 15, 30, 0, 0);
        using var scene = new Scene(document);
        MoveState state = scene.Place(10.5f, -30.5f);
        for (int tick = 0; tick < 120; tick++) state = scene.Step(state, East);
        float face = blockedTile ? 15f : 15f - new TileColliderOptions().WallThickness / 2f;
        Assert.True(state.Position.X <= face - 0.3f + 0.01f, $"crossed solid at {state.Position}");
        Assert.True(state.Position.X >= face - 0.3f - 0.1f, $"stopped short at {state.Position}");
        Assert.Equal(-30.5f, state.Position.Z, 0.05f);
        AssertTerrainSeat(scene, state);
        scene.AssertCompleteGround(30.5f, -30.5f, 0f);
    }

    [Fact]
    public void RegisteredRaisedDeckSupportsAndReleasesAtItsEdge()
    {
        // Catches lost prop support and selected-query terrain falsely holding a body beyond the deck.
        using var scene = new Scene(HighDeckWorld());
        var state = new MoveState { Position = new Vector3(21.5f, 3.25f, -21.5f), Grounded = true };
        for (int tick = 0; tick < 120; tick++)
        {
            state = scene.Step(state, Idle);
            Assert.True(state.Grounded);
            Assert.Equal(3.25f, state.Position.Y, 0.02f);
        }
        bool released = false;
        for (int tick = 0; tick < 120; tick++)
        {
            state = scene.Step(state, East);
            AssertFinite(state);
            if (state.Position.X < 22.7f) Assert.Equal(3.25f, state.Position.Y, 0.02f);
            released |= !state.Grounded;
        }
        Assert.True(released, "the body never released the deck support");
        Assert.True(state.Position.X > 26f, $"stopped on the deck at {state.Position}");
        AssertTerrainSeat(scene, state);
        scene.AssertCompleteGround(30.5f, -30.5f, 0f);
    }

    [Fact]
    public void RegisteredFlatTerrainRetainsQuarterMetreStep()
    {
        // Catches selection losing a later static and a capsule tunnelling through the retained step.
        using var scene = new Scene(FlatWorld());
        scene.World.AddStatic(new BoxShape(new Vector3(3f, 0.125f, 2f)), Pose.At(new Vector3(16f, 0.125f, -30.5f)));
        MoveState state = scene.Place(10.5f, -30.5f);
        for (int tick = 0; tick < 120; tick++)
        {
            state = scene.Step(state, East);
            AssertFinite(state);
            Assert.True(state.Position.Y >= 0.73f, $"tunnelled below terrain at tick {tick}");
            if (state.Position.X > 13.3f)
                Assert.True(state.Position.Y >= 0.98f, $"tunnelled into the step at tick {tick}");
        }
        Assert.True(state.Position.X > 16f && state.Position.X < 18.7f, $"did not arrive on the step at {state.Position}");
        Assert.True(state.Grounded);
        Assert.Equal(1f, state.Position.Y, 0.02f);
        scene.AssertCompleteGround(30.5f, -30.5f, 0f);
    }

    [Fact]
    public void RegisteredFlatTerrainRetainsStandableSlopedProp()
    {
        // Catches a broad exclusion that removes standable prop slopes along with analytic terrain.
        using var scene = new Scene(FlatWorld());
        StaticHandle prop = scene.World.AddStatic(new BoxShape(new Vector3(6f, 0.5f, 6f)),
            new Pose(new Vector3(30.5f, 3f, -30.5f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6f)));
        Assert.True(scene.MovementQueries.Raycast(new Vector3(30.5f, 7f, -30.5f), -Vector3.UnitY, 5f, out RayHit hit));
        Assert.Equal(prop, hit.Body);
        Assert.True(hit.Normal.Y > MathF.Cos(Character.MaxSlopeRadians));
        var state = new MoveState { Position = new Vector3(30.5f, 7f, -30.5f) };
        for (int tick = 0; tick < 120; tick++) state = scene.Step(state, Idle);
        Assert.True(state.Grounded, "did not settle on the sloped prop");
        Assert.True(state.Position.Y > 3.75f, $"fell through the prop to {state.Position}");
        Vector3 settled = state.Position;
        for (int tick = 0; tick < 120; tick++)
        {
            state = scene.Step(state, Idle);
            AssertFinite(state);
            Assert.True(state.Grounded);
            Assert.True(Vector2.Distance(new Vector2(settled.X, settled.Z),
                new Vector2(state.Position.X, state.Position.Z)) < 0.001f);
        }
        scene.AssertCompleteGround(10.5f, -10.5f, 0f);
    }

    [Fact]
    public void NullWorldAndPropsOnlyKeepPointHeightBehavior()
    {
        // Catches a dependency on a query view for the existing analytic-only and props-only paths.
        TileWorldDocument document = Ramp(95);
        document.AddObject("bench", 45, 5, 0, 0);
        using var props = new Scene(TileWorldColliders.Build(document, Catalogs()), includeGround: false);
        MoveState withProps = props.Place(10.5f, -30.5f), terrainOnly = withProps;
        for (int tick = 0; tick < 120; tick++)
        {
            withProps = props.Step(withProps, East);
            terrainOnly = CharacterMovement.Step(terrainOnly, East, Dt, props.Colliders.Ground.HeightDelegate,
                Character, props.Colliders.Ground.NormalDelegate, world: null, clampXz: null,
                medium: props.Colliders.Medium.MediumDelegate);
            AssertEquivalentStates(terrainOnly, withProps);
            AssertTerrainSeat(props, withProps);
        }
        Assert.Equal(8f, withProps.Position.X - 10.5f, 0.02f);
        Assert.True(props.World.Raycast(new Vector3(45.5f, 50f, -5.5f), -Vector3.UnitY, 10f, out _));
    }

    internal static void AssertEquivalentStates(MoveState expected, MoveState actual)
    {
        const float tolerance = 0.00003f;
        Assert.True(Vector3.Distance(expected.Position, actual.Position) <= tolerance);
        Assert.True(Vector2.Distance(expected.HorizontalVelocity, actual.HorizontalVelocity) <= tolerance);
        Assert.True(Vector2.Distance(expected.CommandedVelocity, actual.CommandedVelocity) <= tolerance);
        Assert.Equal(expected.VerticalVelocity, actual.VerticalVelocity, tolerance);
        Assert.Equal(expected.TimeSinceGrounded, actual.TimeSinceGrounded, tolerance);
        Assert.Equal(expected.JumpBufferRemaining, actual.JumpBufferRemaining, tolerance);
        Assert.Equal(expected.ClimbRate, actual.ClimbRate, tolerance);
        Assert.Equal(expected.ClimbRateEwma, actual.ClimbRateEwma, tolerance);
        Assert.Equal(expected.StepDeltaY, actual.StepDeltaY, tolerance);
        Assert.Equal(expected.LandingImpactSpeed, actual.LandingImpactSpeed, tolerance);
        Assert.Equal(expected.FacingYaw, actual.FacingYaw, tolerance);
        Assert.Equal(expected.SpeedScale, actual.SpeedScale, tolerance);
        Assert.Equal(expected.Grounded, actual.Grounded);
        Assert.Equal(expected.SupportGranted, actual.SupportGranted);
        Assert.Equal(expected.Swimming, actual.Swimming);
        Assert.Equal(expected.Commitment, actual.Commitment);
    }

    static TileWorldDocument Ramp(int riseCmPerTile)
    {
        TileWorldDocument document = FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                document.SetCornerHeightCm(x, z, 0, (short)(x * riseCmPerTile));
        return document;
    }

    static void AssertTerrainSeat(Scene scene, MoveState state)
    {
        AssertFinite(state);
        Assert.True(state.Grounded);
        Assert.False(state.Swimming);
        Assert.Equal(scene.Colliders.Ground.HeightAt(state.Position.X, state.Position.Z) + 0.75f, state.Position.Y, 0.02f);
        Assert.False(scene.Colliders.Medium.MediumAt(state.Position.X, state.Position.Z, state.Position.Y - 0.75f).InWater);
    }

    static void AssertFinite(MoveState state)
    {
        Assert.True(float.IsFinite(state.Position.X) && float.IsFinite(state.Position.Y) && float.IsFinite(state.Position.Z));
        Assert.True(float.IsFinite(state.HorizontalVelocity.X) && float.IsFinite(state.HorizontalVelocity.Y));
        Assert.True(float.IsFinite(state.CommandedVelocity.X) && float.IsFinite(state.CommandedVelocity.Y));
        Assert.True(float.IsFinite(state.VerticalVelocity) && float.IsFinite(state.TimeSinceGrounded) &&
            float.IsFinite(state.JumpBufferRemaining) && float.IsFinite(state.ClimbRate) && float.IsFinite(state.ClimbRateEwma) &&
            float.IsFinite(state.StepDeltaY) && float.IsFinite(state.LandingImpactSpeed) && float.IsFinite(state.FacingYaw) &&
            float.IsFinite(state.SpeedScale));
    }

    sealed class Scene : IDisposable
    {
        readonly TileColliderRegistration _registration;

        public Scene(TileWorldDocument document) : this(TileWorldColliders.Build(document, Catalogs())) { }

        public Scene(TileWorldColliders colliders, bool includeGround = true)
        {
            Colliders = colliders;
            _registration = colliders.AddTo(World);
            try
            {
                if (includeGround) MovementQueries = _registration.CreateMovementQueryView();
                else
                {
                    for (int index = 0; index < colliders.Colliders.Count; index++)
                        if (colliders.Colliders[index].Kind == TileColliderKind.Ground)
                            World.RemoveStatic(_registration.Handles[index]);
                    MovementQueries = World;
                }
            }
            catch
            {
                _registration.Dispose();
                World.Dispose();
                throw;
            }
        }

        public TileWorldColliders Colliders { get; }
        public BepuPhysicsWorld World { get; } = new();
        public IPhysicsWorld MovementQueries { get; }
        public MoveState Place(float x, float z) => new()
        {
            Position = new Vector3(x, Colliders.Ground.HeightAt(x, z) + 0.75f, z),
            Grounded = true
        };
        public MoveState Step(MoveState state, MoveCommand command) => CharacterMovement.Step(state, command, Dt,
            Colliders.Ground.HeightDelegate, Character, Colliders.Ground.NormalDelegate, MovementQueries, null,
            Colliders.Medium.MediumDelegate);

        public void AssertCompleteGround(float x, float z, float height)
        {
            Assert.True(World.Raycast(new Vector3(x, height + 3f, z), -Vector3.UnitY, 4f, out RayHit hit));
            Assert.Equal(height, hit.Point.Y, 0.001f);
            Assert.Contains(hit.Body!.Value, _registration.GroundHandles);
        }

        public void Dispose()
        {
            if (MovementQueries is IPhysicsWorldQueryView selected) selected.Dispose();
            _registration.Dispose();
            World.Dispose();
        }
    }
}

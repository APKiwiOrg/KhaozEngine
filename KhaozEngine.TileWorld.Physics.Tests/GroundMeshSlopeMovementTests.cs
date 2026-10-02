using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.TileWorld.Physics.TileWorldPhysicsTestData;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class GroundMeshSlopeMovementTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    const float Grade = 0.95f;
    const float StartX = 10.5f, StartZ = -30.5f;
    const int Ticks = 120, FinalWindow = 30;
    static readonly MoveTuning Character = new(WalkSpeed: 2f, RunSpeed: 5f,
        CapsuleHalfHeight: 0.75f, MaxSlopeRadians: MathF.PI / 4f, CapsuleRadius: 0.3f, StepHeight: 0.4f);
    static readonly MoveCommand Uphill = new(new Vector2(0f, 1f), run: false, cameraYaw: -MathF.PI / 2f);

    [Fact]
    public void WalkableGroundMeshKeepsClimbingAtGrimhollow30Hz()
    {
        // Catches a walkable terrain mesh consuming uphill travel while the analytic ground still grants footing.
        TileWorldColliders colliders = TileWorldColliders.Build(SteepDryRamp(), Catalogs());
        using var mesh = new Scene(colliders, includeGround: true, output);
        using var control = new Scene(colliders, includeGround: false, output);
        Assert.True(mesh.OtherStatics > 0, "the paired fixture must retain a non-ground static");
        Assert.Equal(mesh.OtherStatics, control.OtherStatics);
        AssertGroundFixture(mesh);
        Assert.False(control.World.Raycast(new Vector3(StartX, 13f, StartZ), -Vector3.UnitY, 4f, out _),
            "the control still has ground at the interior probe");
        Assert.True(control.World.Raycast(new Vector3(45.5f, 50f, -5.5f), -Vector3.UnitY, 10f, out RayHit retained),
            "omitting ground removed the distant bench");
        Assert.Equal("Object", control.World.KindOf(retained.Body));

        MoveState start = new()
        {
            Position = new Vector3(StartX, colliders.Ground.HeightAt(StartX, StartZ) + Character.CapsuleHalfHeight, StartZ),
            Grounded = true
        };

        TickSample[] omitted = Walk(control, start);
        WriteSummary("ground omitted", start, omitted);
        AssertStates(control, omitted, maxClearance: 0.02f);
        // Four seconds at walk 2 independently gives 8 m, including 2 m in the final second.
        Assert.Equal(8f, omitted[^1].State.Position.X - StartX, 0.02f);
        Assert.Equal(2f, omitted[^1].State.Position.X - omitted[Ticks - FinalWindow - 1].State.Position.X, 0.02f);

        TickSample[] present = Walk(mesh, start);
        WriteSummary("ground present", start, present);
        float tangentClearance = Character.CapsuleRadius * (MathF.Sqrt(1f + Grade * Grade) - 1f);
        AssertStates(mesh, present, tangentClearance + 0.02f);
        float advance = present[^1].State.Position.X - StartX;
        float finalAdvance = present[^1].State.Position.X - present[Ticks - FinalWindow - 1].State.Position.X;
        Assert.True(advance >= 0.5f, $"ground mesh uphill advance {advance:F5} m over {Ticks} ticks");
        Assert.True(finalAdvance >= 0.1f,
            $"ground mesh stalled in the final {FinalWindow} ticks, uphill advance {finalAdvance:F5} m");
        Assert.True(present[^1].State.Position.Y - start.Position.Y >= 0.455f,
            $"ground mesh rose only {present[^1].State.Position.Y - start.Position.Y:F5} m");
    }

    [Theory]
    [InlineData(75, false, 30)]
    [InlineData(95, true, 30)]
    [InlineData(55, false, 30)]
    [InlineData(95, false, 60)]
    public void SelectedRegisteredSlopeMatchesGroundOmittedControl(int riseCmPerTile, bool run, int hz)
    {
        // Catches duplicate terrain correction at the bounded grades, speeds and tick rates.
        TileWorldColliders colliders = TileWorldColliders.Build(SteepDryRamp(riseCmPerTile), Catalogs());
        using var mesh = new Scene(colliders, includeGround: true, output);
        using var control = new Scene(colliders, includeGround: false, output);
        float height = 10.25f * riseCmPerTile * 0.01f;
        Assert.True(mesh.World.Raycast(new Vector3(10.25f, height + 3f, StartZ), -Vector3.UnitY, 4f, out RayHit ground));
        Assert.Equal("Ground", mesh.World.KindOf(ground.Body));
        Assert.Equal(height, ground.Point.Y, 0.001f);
        Assert.Equal(mesh.OtherStatics, control.OtherStatics);
        Assert.True(mesh.OtherStatics > 0);
        var start = new MoveState
        {
            Position = new Vector3(StartX, colliders.Ground.HeightAt(StartX, StartZ) + Character.CapsuleHalfHeight, StartZ),
            Grounded = true
        };
        var command = new MoveCommand(new Vector2(0f, 1f), run, -MathF.PI / 2f);
        float dt = 1f / hz, speed = run ? 5f : 2f;
        TickSample[] present = Walk(mesh, start, command, dt);
        TickSample[] omitted = Walk(control, start, command, dt);
        AssertStates(mesh, present, 0.02f, speed * dt);
        AssertStates(control, omitted, 0.02f, speed * dt);
        Assert.Equal(speed * Ticks / hz, omitted[^1].State.Position.X - StartX, 0.02f);
        Assert.Equal(speed * FinalWindow / hz,
            omitted[^1].State.Position.X - omitted[Ticks - FinalWindow - 1].State.Position.X, 0.02f);
        for (int tick = 0; tick < Ticks; tick++)
            GroundMeshQueryOwnershipTests.AssertEquivalentStates(omitted[tick].State, present[tick].State);
        WriteSummary($"selected {riseCmPerTile} cm, run={run}, {hz} Hz", start, present);
    }

    static TileWorldDocument SteepDryRamp(int riseCmPerTile = 95)
    {
        TileWorldDocument document = FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                document.SetCornerHeightCm(x, z, 0, (short)(x * riseCmPerTile));
        // Away from the walking corridor, so omitting ground must still preserve a real prop static.
        document.AddObject("bench", 45, 5, 0, 0);
        return document;
    }

    void AssertGroundFixture(Scene scene)
    {
        // An interior point off the triangle diagonal independently checks the authored 0.95 grade plane.
        const float x = 10.25f, z = -30.5f, height = 9.7375f;
        Vector3 expectedNormal = Vector3.Normalize(new Vector3(-0.95f, 1f, 0f));
        Assert.Equal(height, scene.Colliders.Ground.HeightAt(x, z), 1e-4f);
        Assert.True(scene.World.Raycast(new Vector3(x, height + 3f, z), -Vector3.UnitY, 4f, out RayHit hit),
            "the registered ground mesh has no downward interior hit");
        output.WriteLine($"fixture ray: distance={hit.Distance:F5} point={hit.Point} normal={hit.Normal} body={hit.Body}");
        Assert.True(hit.Body.HasValue && scene.World.KindOf(hit.Body) == "Ground", "the fixture ray did not hit ground");
        Assert.Equal(3f, hit.Distance, 1e-3f);
        Assert.True(Vector3.Distance(hit.Point, new Vector3(x, height, z)) < 1e-3f, $"wrong plane hit {hit.Point}");
        Assert.True(Vector3.Distance(hit.Normal, expectedNormal) < 1e-3f, $"wrong ground normal {hit.Normal}");
        Assert.True(hit.Normal.Y > MathF.Cos(Character.MaxSlopeRadians), "the fixture is not walkable");
        Assert.False(scene.Colliders.Medium.MediumAt(x, z, height).InWater, "the fixture is wet");
    }

    static TickSample[] Walk(Scene scene, MoveState state) => Walk(scene, state, Uphill, Dt);

    static TickSample[] Walk(Scene scene, MoveState state, MoveCommand command, float dt)
    {
        var samples = new TickSample[Ticks];
        for (int tick = 0; tick < Ticks; tick++)
        {
            scene.World.BeginTick();
            MoveState previous = state;
            float floor = scene.Colliders.Ground.HeightAt(state.Position.X, state.Position.Z);
            if (scene.World.Recording)
                scene.World.Raycast(new Vector3(state.Position.X, floor + 3f, state.Position.Z), -Vector3.UnitY, 4f, out _);
            state = CharacterMovement.Step(state, command, dt, scene.Colliders.Ground.HeightDelegate, Character,
                scene.Colliders.Ground.NormalDelegate, scene.MovementQueries, null, scene.Colliders.Medium.MediumDelegate);
            samples[tick] = new TickSample(previous.Position, state);
            scene.World.EndTick(tick, previous, state, scene.Colliders.Ground);
        }
        return samples;
    }

    void WriteSummary(string label, MoveState start, TickSample[] samples)
    {
        Vector3 end = samples[^1].State.Position;
        float finalAdvance = end.X - samples[Ticks - FinalWindow - 1].State.Position.X;
        output.WriteLine($"{label}: start={start.Position} end={end} advance={end.X - start.Position.X:F5} " +
            $"rise={end.Y - start.Position.Y:F5} final-window-advance={finalAdvance:F5}");
    }

    static void AssertStates(Scene scene, TickSample[] samples, float maxClearance, float commandedTravel = 2f / 30f)
    {
        for (int tick = 0; tick < samples.Length; tick++)
        {
            MoveState state = samples[tick].State;
            Vector3 at = state.Position;
            Assert.True(Finite(at) && Finite(new Vector3(state.HorizontalVelocity, state.VerticalVelocity)) &&
                float.IsFinite(state.CommandedVelocity.X) && float.IsFinite(state.CommandedVelocity.Y) &&
                float.IsFinite(state.TimeSinceGrounded) && float.IsFinite(state.FacingYaw) &&
                float.IsFinite(state.ClimbRate) && float.IsFinite(state.ClimbRateEwma) &&
                float.IsFinite(state.JumpBufferRemaining) && float.IsFinite(state.StepDeltaY) &&
                float.IsFinite(state.LandingImpactSpeed) && float.IsFinite(state.SpeedScale),
                $"tick {tick}: non-finite state at {at}");
            Assert.True(state.Grounded, $"tick {tick}: airborne at {at}");
            Assert.False(state.Swimming, $"tick {tick}: swimming on the dry ramp");
            Assert.False(scene.Colliders.Medium.MediumAt(at.X, at.Z, at.Y - Character.CapsuleHalfHeight).InWater,
                $"tick {tick}: entered water on the dry ramp");
            float clearance = at.Y - Character.CapsuleHalfHeight - scene.Colliders.Ground.HeightAt(at.X, at.Z);
            Assert.True(clearance >= -0.02f && clearance <= maxClearance,
                $"tick {tick}: ground-relative feet clearance {clearance:F5} outside [-0.02, {maxClearance:F5}]");
            Assert.Equal(StartZ, at.Z, 0.05f);
            float forward = at.X - samples[tick].Start.X;
            Assert.True(forward <= commandedTravel + 0.06f, $"tick {tick}: forward teleport of {forward:F5} m");
        }
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    readonly record struct TickSample(Vector3 Start, MoveState State);

    sealed class Scene : IDisposable
    {
        readonly TileColliderRegistration _registration;

        public Scene(TileWorldColliders colliders, bool includeGround, ITestOutputHelper output)
        {
            Colliders = colliders;
            BepuPhysicsWorld? groundOnly = includeGround ? new BepuPhysicsWorld() : null;
            World = new DiagnosticWorld(new BepuPhysicsWorld(), groundOnly, output);
            _registration = colliders.AddTo(World);
            for (int index = 0; index < colliders.Colliders.Count; index++)
            {
                TileCollider collider = colliders.Colliders[index];
                StaticHandle handle = _registration.Handles[index];
                World.Kinds.Add(handle, collider.Kind);
                if (collider.Kind == TileColliderKind.Ground)
                {
                    if (includeGround) groundOnly!.AddStatic(collider.Shape, collider.Pose);
                    else World.RemoveStatic(handle);
                }
                else OtherStatics++;
            }
            try
            {
                MovementQueries = includeGround ? _registration.CreateMovementQueryView() : World;
                if (MovementQueries is IPhysicsWorldQueryView selected) Assert.Same(World, selected.SourceWorld);
            }
            catch
            {
                _registration.Dispose();
                World.Dispose();
                throw;
            }
        }

        public TileWorldColliders Colliders { get; }
        public DiagnosticWorld World { get; }
        public IPhysicsWorld MovementQueries { get; }
        public int OtherStatics { get; }

        public void Dispose()
        {
            if (MovementQueries is IPhysicsWorldQueryView selected) selected.Dispose();
            _registration.Dispose();
            World.Dispose();
        }
    }

    // Records complete and selected queries. Ground-only queries attribute the seam's unlabelled MTV.
    class DiagnosticWorld(IPhysicsWorld inner, BepuPhysicsWorld? groundOnly, ITestOutputHelper output,
        DiagnosticWorld? recorder = null) : IPhysicsWorld
    {
        const int WindowTicks = 8, QueriesPerTick = 64;
        readonly List<string> _queries = new();
        bool _windowStarted, _complete, _zeroNormal;
        int _remaining, _omitted;
        readonly Dictionary<StaticHandle, TileColliderKind> _kinds = new();
        DiagnosticWorld Recorder => recorder ?? this;
        public Dictionary<StaticHandle, TileColliderKind> Kinds => Recorder._kinds;
        public bool Recording => groundOnly is not null && !Recorder._complete;

        public string KindOf(StaticHandle? handle) => handle is { } h && Kinds.TryGetValue(h, out TileColliderKind kind)
            ? kind.ToString() : "unknown";

        public void BeginTick()
        {
            _queries.Clear();
            _zeroNormal = false;
            _omitted = 0;
        }

        public void EndTick(int tick, MoveState start, MoveState end, TileGroundSampler ground)
        {
            if (!Recording) return;
            float advance = end.Position.X - start.Position.X;
            if (!_windowStarted && (advance < 2f / 30f * 0.25f || _zeroNormal))
            {
                _windowStarted = true;
                _remaining = WindowTicks;
            }
            if (!_windowStarted) return;
            float beforeFloor = ground.HeightAt(start.Position.X, start.Position.Z);
            float afterFloor = ground.HeightAt(end.Position.X, end.Position.Z);
            output.WriteLine($"trace tick {tick}: start={start.Position} end={end.Position} " +
                $"commanded-delta={end.CommandedVelocity * Dt} achieved={end.Position - start.Position} " +
                $"start-floor={beforeFloor:F5} end-floor={afterFloor:F5} " +
                $"start-clearance={start.Position.Y - Character.CapsuleHalfHeight - beforeFloor:F5} " +
                $"end-clearance={end.Position.Y - Character.CapsuleHalfHeight - afterFloor:F5} " +
                $"normal={ground.NormalAt(start.Position.X, start.Position.Z)} grounded={start.Grounded}->{end.Grounded}");
            foreach (string query in _queries) output.WriteLine(query);
            if (_omitted > 0) output.WriteLine($"  omitted queries in this tick: {_omitted}");
            if (--_remaining == 0) _complete = true;
        }

        void Record(string query)
        {
            if (!Recording) return;
            if (Recorder._queries.Count < QueriesPerTick) Recorder._queries.Add(query);
            else Recorder._omitted++;
        }

        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
        {
            bool found = inner.ComputePenetration(capsule, pose, out mtv);
            if (Recording && Recorder._queries.Count < QueriesPerTick)
            {
                bool groundFound = groundOnly!.ComputePenetration(capsule, pose, out Vector3 groundMtv);
                string probe = capsule.Radius > Character.CapsuleRadius + 0.005f ? "inflated" : "body";
                Record($"  penetration {probe}: pose={pose.Position} radius={capsule.Radius:F5} length={capsule.Length:F5} " +
                    $"found={found} mtv={mtv} ground-only-found={groundFound} ground-only-mtv={groundMtv}");
            }
            else if (Recording) Recorder._omitted++;
            return found;
        }

        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance,
            out SweepHit hit, QueryFilter filter = default)
        {
            bool found = inner.SweepCapsule(capsule, pose, direction, maxDistance, out hit, filter);
            if (Recording)
            {
                Recorder._zeroNormal |= found && hit.Normal.LengthSquared() <= 1e-12f;
                string probe = direction.Y < -0.999f ? "downward" :
                    MathF.Abs(maxDistance - 0.9f) < 1e-4f ? "recovery-range" : "move-or-step";
                Record($"  sweep {probe}: pose={pose.Position} radius={capsule.Radius:F5} length={capsule.Length:F5} " +
                    $"direction={direction} range={maxDistance:F5} found={found} distance={hit.Distance:F5} " +
                    $"normal={hit.Normal} point={hit.Point} body={hit.Body} kind={KindOf(hit.Body)}");
            }
            return found;
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default)
        {
            bool found = inner.Raycast(origin, direction, maxDistance, out hit, filter);
            if (Recording)
                Record($"  ray: origin={origin} direction={direction} range={maxDistance:F5} found={found} " +
                    $"distance={hit.Distance:F5} normal={hit.Normal} point={hit.Point} body={hit.Body} kind={KindOf(hit.Body)}");
            return found;
        }

        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) =>
            inner.AddStatic(shape, pose, material);
        public IPhysicsWorldQueryView CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle> excludedStatics) =>
            new DiagnosticQueryView(inner.CreateQueryViewExcludingStatics(excludedStatics), this, groundOnly, output);
        public void RemoveStatic(StaticHandle handle) => inner.RemoveStatic(handle);
        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
            PhysicsMaterial? material = null) => inner.AddDynamic(shape, pose, body, material);
        public void RemoveDynamic(DynamicBodyHandle handle) => inner.RemoveDynamic(handle);
        public Pose GetDynamicPose(DynamicBodyHandle handle) => inner.GetDynamicPose(handle);
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) =>
            inner.GetDynamicVelocity(handle, out linear, out angular);
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) =>
            inner.SetDynamicVelocity(handle, linear, angular);
        public bool IsAwake(DynamicBodyHandle handle) => inner.IsAwake(handle);
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => inner.AddConstraint(description);
        public void RemoveConstraint(ConstraintHandle handle) => inner.RemoveConstraint(handle);
        public void SetConstraintTarget(ConstraintHandle handle, float target) => inner.SetConstraintTarget(handle, target);
        public void Step(float dt) => inner.Step(dt);
        public Vector3 Origin => inner.Origin;
        public bool CanRebase => inner.CanRebase;
        public void Rebase(Vector3 newOrigin) => inner.Rebase(newOrigin);
        public void Dispose()
        {
            if (recorder is null) groundOnly?.Dispose();
            inner.Dispose();
        }
    }

    sealed class DiagnosticQueryView : DiagnosticWorld, IPhysicsWorldQueryView
    {
        public DiagnosticQueryView(IPhysicsWorldQueryView inner, DiagnosticWorld source,
            BepuPhysicsWorld? groundOnly, ITestOutputHelper output) : base(inner, groundOnly, output, source)
        {
            SourceWorld = source;
        }

        public IPhysicsWorld SourceWorld { get; }
    }
}

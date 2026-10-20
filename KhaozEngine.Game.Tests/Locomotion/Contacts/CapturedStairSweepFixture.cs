using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using KhaozEngine.Tests.Physics;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Portable authored hulls, independently captured installed faces, and a real acquired dry interval.</summary>
internal sealed class CapturedStairSweepFixture : IDisposable
{
    internal const float PlacementOffset = 0.001f;
    internal static readonly MovementSpaceKey Room = new("world", "room");
    internal static readonly MovementQueryIdentity QueryIdentity = new("certified-general-sweeps", 1, "captured-stairs");
    internal readonly SupportFootprintScene Scene = new();
    readonly IPhysicsWorldQueryView restricted;
    readonly Dictionary<(StaticHandle, int), MovementSupportKey> registration = [];
    readonly long generation;
    readonly Quaternion yaw;
    internal BepuPhysicsWorld World => Scene.World;
    internal CountingQueryView View { get; }
    internal EnvironmentAcquisitionFixture Producer { get; }
    internal MovementQueryScope Scope { get; }
    internal MoveTuning Tuning { get; } = MoveTuning.Default with
    {
        CapsuleRadius = 0.3f,
        CapsuleHalfHeight = 0.75f,
        StepHeight = 0.4f,
        FootRadiusFraction = 0.5f,
    };
    internal readonly List<StaticHandle> StairHandles = [];
    internal StaticHandle Approach { get; }
    internal StaticHandle Landing { get; }
    internal readonly List<(MovementContactSupportRequest Request, MovementContactSupportBinding Binding)> Bindings = [];
    internal bool RecordBindings;
    internal MovementAvailability LastAcquisitionStatus;
    internal MovementQueryLease? LastLease;

    internal CapturedStairSweepFixture(bool compound, Quaternion yaw, Vector3 origin)
    {
        this.yaw = yaw;
        using JsonDocument capture = ReadCapture();
        PhysicsShape[] hulls = capture.RootElement.GetProperty("parts").EnumerateArray()
            .Select(part => (PhysicsShape)new ConvexHullShape(part.GetProperty("points").EnumerateArray()
                .Select(Point).ToArray())).ToArray();
        if (compound)
            StairHandles.Add(Scene.Install(new CompoundShape(hulls.Select(h => new CompoundChild(h, Pose.Identity)).ToArray()),
                new(Vector3.Zero, yaw)));
        else
            foreach (PhysicsShape hull in hulls) StairHandles.Add(Scene.Install(hull, new(Vector3.Zero, yaw)));
        StaticHandle approach = Approach = Pad(-1.5f, 0), landing = Landing = Pad(1.5f, 1);
        World.Rebase(origin);
        foreach (StaticHandle handle in StairHandles.Append(approach).Append(landing)) Scene.Capture(handle);
        foreach (OracleFace face in Scene.Faces)
            registration.Add((face.Static, face.ElementId), new("world",
                face.Static == approach ? "approach-pad" : face.Static == landing ? "landing-pad" : "stairs"));
        generation = World.StaticGeometryGeneration;
        restricted = World.CreateQueryViewExcludingStatics([]);
        View = new CountingQueryView(restricted);
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, World.Origin, 1);
        Scope = new(new(-1500, -30, -30), new(1500, 30, 30), 20, 20, Room, QueryIdentity, frame);
        Producer = CreateProducer(Scope);
    }

    StaticHandle Pad(float z, float top) => Scene.Install(new BoxShape(new(0.5f, 0.125f, 0.5f)),
        new(Vector3.Transform(new Vector3(0, top - 0.125f, z), yaw), yaw));

    internal static JsonDocument ReadCapture() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Locomotion", "Contacts", "Fixtures", "grimhollow-stairs-collision.json")));
    internal static Vector3 Point(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
    internal Vector3 Rotate(Vector3 point) => Vector3.Transform(point, yaw);

    internal EnvironmentAcquisitionFixture CreateProducer(MovementQueryScope scope)
    {
        var producer = new EnvironmentAcquisitionFixture(View, scope) { ContactSupportEnabled = true };
        producer.OnPrepare = () =>
        {
            if (World.StaticGeometryGeneration != generation) producer.Fault = "stale-pin";
        };
        producer.OnBodySample = (in MovementBodyQuery _) => new(MovementAvailability.Known, Room, null, false, 0, null);
        producer.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            spans[0] = new(0, 1, 0, 0, true);
            return new(MovementAvailability.Known, 1, 1, 0, 0, 0, QueryIdentity);
        };
        producer.OnAnalyticGround = (in MovementSupportRequest _, out MovementAnalyticGround? ground) =>
        {
            ground = null;
            return MovementAvailability.Known;
        };
        producer.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
        {
            binding = default;
            if (World.StaticGeometryGeneration != generation || request.Static is not { } handle ||
                !registration.TryGetValue((handle, request.FeatureId), out var owner)) return MovementAvailability.Unresolved;
            binding = new(owner, Room, null, QueryIdentity);
            if (RecordBindings) Bindings.Add((request, binding));
            return MovementAvailability.Known;
        };
        return producer;
    }

    internal void InvalidateRegistration() => registration.Clear();

    // Input feet are absolute world support feet. The explicit placement offset belongs to the body only.
    internal FramedMovementState StateAtFeet(Vector3 feet) => new(new MoveState
    {
        Position = feet - Scope.Frame.PhysicsOrigin + new Vector3(0, Tuning.CapsuleHalfHeight, 0) + new Vector3(0, PlacementOffset, 0),
        Grounded = true,
    }, Scope.Frame, new(Room, null, QueryIdentity));

    internal Vector3 SupportFeet(in FramedMovementState state) =>
        state.State.Position - new Vector3(0, Tuning.CapsuleHalfHeight, 0) - new Vector3(0, PlacementOffset, 0) + state.Frame.PhysicsOrigin;

    internal MovementStepResult StepTowards(in FramedMovementState state, Vector2 direction, float dt)
    {
        var acquired = Producer.Acquire();
        LastAcquisitionStatus = acquired.Status;
        if (acquired.Status != MovementAvailability.Known)
            throw new InvalidOperationException($"Captured stair acquisition {acquired.Status}.");
        using var lease = (MovementQueryLease)acquired.Lease!;
        LastLease = lease;
        return StepWithLease(state, direction, dt, lease);
    }

    internal MovementStepResult StepWithLease(in FramedMovementState state, Vector2 direction, float dt, MovementQueryLease lease) =>
        ExplicitCharacterMovement.StepTowards(state, direction, false, dt, Tuning, new(WaterTraversalMode.DryOnly), lease);

    internal FootprintOracleSupport Expected(in FootSupportQuery query, bool guarded)
    {
        var request = SupportFootprintTests.Request(query.Axis.X, query.Axis.Y, query.FootRadius, query.CosMaxSlope,
            (double)query.FeetY - query.ReachDown, (double)query.FeetY + query.ReachUp);
        if (guarded && FootSupportGuardOracle.Refuses(Scene, request))
            return new(SupportStatus.Refused, double.NaN, double.NaN, double.NaN, default, default, null, -1);
        var members = SupportFootprintTests.Members(Scene, request);
        var expected = SupportFootprintOracle.Select(members, request);
        if (guarded) FootSupportGuardOracle.RequireModelled(Scene, request, members, expected);
        return expected;
    }

    public void Dispose()
    {
        restricted.Dispose();
        Scene.Dispose();
    }
}

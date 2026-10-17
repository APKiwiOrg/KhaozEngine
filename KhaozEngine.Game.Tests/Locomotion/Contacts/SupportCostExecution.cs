using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using KhaozEngine.Tests.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

internal readonly record struct SupportCostOutput(bool GeometryPassed, string Outcome, double Height,
    double Error, Vector3 Motion, bool Refused)
{
    public MoveState? State { get; init; }
    public SupportStatus? Status { get; init; }
    public MovementStepOutcome? StepOutcome { get; init; }
    public Vector3 EndFeet { get; init; }
    public bool GeometryMatchesOracle { get; init; }
    /// <summary>The end placement was judged as a paced climb.</summary>
    public bool Paced { get; init; }
}

/// <summary>One operation's scene, selected view and identical immutable input for cold and warm calls.</summary>
internal sealed class SupportCostExecution : IDisposable
{
    readonly SupportCostRow row;
    readonly string operation;
    readonly SupportFootprintScene scene;
    readonly IPhysicsQueryLease? borrowed;
    readonly MovementQueryLease? movementBorrowed;
    readonly CapsuleShape capsule;
    readonly IPhysicsWorldQueryView inner;
    readonly FootprintOracleSupport expected;
    readonly Func<float, float, float> height;
    readonly Func<float, float, Vector3> normal;
    readonly Func<float, float, Vector2>? clamp;
    readonly EnvironmentAcquisitionFixture? environment;
    readonly MovementBoundary? boundary;
    static readonly MovementSpaceKey Room = new("world", "room");
    internal MovementQueryLease? LastMovementLease { get; private set; }
    internal EnvironmentAcquisitionFixture Producer => environment!;
    internal CountingQueryView View { get; }
    internal KhaozEngine.Physics.Bepu.BepuPhysicsWorld World => scene.World;
    internal Func<SupportCostOutput> Invoke { get; }
    internal FootprintOracleSupport Expected => expected;
    internal long ScratchPayloadBytes => SupportCostScratch.PayloadBytes(scene.World);

    internal SupportCostExecution(SupportCostRow row, string operation)
    {
        this.row = row;
        this.operation = operation;
        scene = SupportGateCostScenes.Build(row);
        expected = operation == "LegacyTreadDownPass"
            ? SupportGateCostScenes.Expected(scene, row, row.Query with { Axis = new(0.175f, 0), FeetY = 0 }, guarded: false)
            : SupportGateCostScenes.Expected(scene, row, guarded: !operation.StartsWith("Legacy", StringComparison.Ordinal));
        inner = scene.World.CreateQueryViewExcludingStatics([]);
        View = new(inner);
        capsule = CharacterMovement.CapsuleFor(SupportGateCostScenes.Tuning);
        if (operation is "Find" or "GroundTickBorrowed" or "LegacyDownPass" or "LegacyTreadDownPass")
            borrowed = View.AcquireQueryReadLease();
        height = (x, _) => row.HeightAt(x);
        normal = (x, _) => row.NormalAt(x);
        if (row.FinalClamp)
        {
            float maximum = float.IsNaN(row.ClampMaximumX) ? row.Feet.X + 0.19f : row.ClampMaximumX;
            clamp = (x, z) => new(MathF.Min(x, maximum), z);
            boundary = MovementBoundary.Rectangle(-1500, -10, maximum, 10);
        }
        if (row.Explicit) environment = Environment();
        if (operation == "ExplicitTickBorrowed") movementBorrowed = (MovementQueryLease)environment!.Acquire().Lease!;
        Invoke = Execute;
    }

    SupportCostOutput Execute()
    {
        MoveTuning tuning = SupportGateCostScenes.Tuning;
        MoveState input = row.Input;
        switch (operation)
        {
            case "Find":
                {
                    IPhysicsQueryLease lease = borrowed!;
                    SupportSample support = FootSupport.Find(row.Analytic ? height : null,
                        row.Analytic ? normal : null, View, lease, row.Query);
                    return new(false, SupportOutcome(support.Status), support.Height, support.HeightError,
                        Vector3.Zero, support.Status == SupportStatus.Refused)
                    { Status = support.Status, EndFeet = row.Feet };
                }
            case "GroundTickBorrowed":
                {
                    IPhysicsQueryLease lease = borrowed!;
                    GroundStepResult step = GroundCore.Step(row.Feet, row.Direction * tuning.WalkSpeed * row.Dt,
                        row.Dt, tuning, new(), DryQueries.For(row.Analytic ? height : null,
                            row.Analytic ? normal : null, View, lease, tuning));
                    bool passed = step.Footing == GroundFooting.Walkable &&
                        (row.Direction == Vector2.Zero || step.Achieved.LengthSquared() > 0);
                    return new(passed, GroundOutcome(step.Footing), step.Feet.Y, step.Support.HeightError,
                        step.Feet - row.Feet, step.Footing == GroundFooting.Held)
                    { EndFeet = step.Feet };
                }
            case "LegacyDownPass":
            case "LegacyTreadDownPass":
                {
                    float terrainCentre = row.TerrainHeight + tuning.CapsuleHalfHeight;
                    Vector3 pos = operation == "LegacyTreadDownPass" ? new(0.175f, 0.9f, 0) : input.Position;
                    Vector3 previous = operation == "LegacyTreadDownPass" ? new(0.175f, 1.15f, 0) : input.Position;
                    if (operation == "LegacyTreadDownPass") terrainCentre = 0.9f;
                    float ground = CharacterMovement.MeasureSupportDownPass(View, capsule, pos,
                        previous, input.Grounded, tuning, tuning.CapsuleHalfHeight, terrainCentre,
                        terrainCentre, row.StairSignal);
                    double feet = ground - tuning.CapsuleHalfHeight;
                    return new(double.IsFinite(expected.Height) && Math.Abs(feet - expected.Height) <= 0.001,
                        "LegacyDownPass", feet, 0, Vector3.Zero, false);
                }
            case "LegacyTick":
                using (IPhysicsQueryLease lease = View.AcquireQueryReadLease())
                    return TickOutput(CharacterMovement.StepTowards(input, row.Direction, false, row.Dt,
                        height, tuning, normal, View, clamp));
            case "DryTick":
                return TickOutput(ContactController.Step(input, row.Direction, row.Direction == Vector2.Zero ? 0 : 1,
                    false, false, row.Dt, height, tuning, normal, View, clamp, null));
            case "ExplicitTick":
                (MovementAvailability Status, IDisposable? Lease) acquired;
                using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.Acquisition))
                    acquired = environment!.Acquire();
                if (acquired.Status != MovementAvailability.Known)
                    return new(false, "AcquisitionRefused", double.NaN, double.NaN, Vector3.Zero, true);
                MovementQueryLease acquiredLease = (MovementQueryLease)acquired.Lease!;
                try { return ExplicitTick(acquiredLease); }
                finally
                {
                    using var disposal = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.Disposal);
                    acquiredLease.Dispose();
                }
            case "ExplicitTickBorrowed":
                return ExplicitTick(movementBorrowed!);
            case "Lease":
                using (IPhysicsQueryLease lease = View.AcquireQueryReadLease())
                    return new(true, "Acquired", 0, 0, Vector3.Zero, false);
            default: throw new InvalidOperationException(operation);
        }
    }

    SupportCostOutput ExplicitTick(MovementQueryLease lease)
    {
        LastMovementLease = lease;
        var state = new FramedMovementState(row.Input, lease.Frame, new(Room, null, lease.Identity));
        MovementStepResult result = ExplicitCharacterMovement.StepTowards(state, row.Direction, false,
            row.Dt, SupportGateCostScenes.Tuning, new(row.WaterMode), lease, boundary);
        SupportCostOutput output = TickOutput(result.State.State);
        return output with
        {
            Outcome = result.Outcome == MovementStepOutcome.Advanced ? "Advanced" : result.Outcome == MovementStepOutcome.Blocked ? "Blocked" : "ExplicitRefused",
            GeometryPassed = output.GeometryPassed && result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked,
            Refused = result.Outcome is not (MovementStepOutcome.Advanced or MovementStepOutcome.Blocked),
            StepOutcome = result.Outcome
        };
    }

    SupportCostOutput TickOutput(MoveState next)
    {
        float feet = next.Position.Y - 0.9f - (row.Explicit ? 0.001f : 0);
        Vector3 motion = next.Position - row.Input.Position;
        bool finite = float.IsFinite(next.Position.X) && float.IsFinite(next.Position.Y) && float.IsFinite(next.Position.Z);
        bool moved = row.Direction == Vector2.Zero || new Vector2(motion.X, motion.Z).LengthSquared() > 0;
        bool geometry = finite && moved && (next.Grounded || next.Swimming || !row.Grounded);
        if (row.Name.StartsWith("flat ", StringComparison.Ordinal) || row.Name is "analytic only" or
            "analytic plus props" or "stationary" or "initial" or "stacked irrelevant layers" or "explicit entry" or
            "explicit wade")
            geometry &= next.Grounded && Math.Abs(feet - row.Feet.Y) <= 0.001;
        return new(geometry, next.Swimming ? "Swimming" : next.Grounded ? "Grounded" : "Airborne",
            feet, 0, motion, false)
        { State = next, EndFeet = new(next.Position.X, feet, next.Position.Z) };
    }

    internal FootprintOracleSupport CheckFind(in SupportFindObservation observation, out bool matches)
    {
        var q = new FootSupportQuery(observation.Axis, observation.FeetY, observation.Radius,
            observation.ReachUp, observation.ReachDown, observation.CosMaxSlope);
        FootprintOracleSupport oracle = SupportGateCostScenes.Expected(scene, row, q, guarded: true);
        SupportStatus status = (SupportStatus)observation.Status;
        matches = status == oracle.Status && (status is SupportStatus.None or SupportStatus.Refused ||
            Math.Abs(observation.Height - oracle.Height) <= observation.HeightError &&
            observation.HeightError <= 0.0005f);
        return oracle;
    }

    internal SupportCostOutput CheckGeometry(SupportCostOutput output, bool readsMatch)
    {
        if (operation == "Find")
            return output with
            {
                GeometryMatchesOracle = readsMatch,
                GeometryPassed = readsMatch && output.Status is SupportStatus.Walkable or SupportStatus.Steep,
            };
        if (operation is "DryTick" or "ExplicitTick" or "GroundTickBorrowed" or "LegacyTick")
        {
            bool placed = true, paced = false;
            bool supported = output.State is { } state ? state.Grounded || state.SupportGranted : output.GeometryPassed;
            if (supported)
            {
                var q = row.Query with { Axis = new(output.EndFeet.X, output.EndFeet.Z), FeetY = output.EndFeet.Y };
                // Placement asks where the body ended, a geometric question rather than a FootSupport read.
                FootprintOracleSupport final = SupportGateCostScenes.Expected(scene, row, q, guarded: false);
                paced = Paced(row, final.Height);
                double allowedLag = paced ? SupportGateCostScenes.Tuning.StepHeight : 0.001;
                placed = final.Status is SupportStatus.Walkable or SupportStatus.Steep &&
                    output.EndFeet.Y <= final.Height + 0.001 && final.Height - output.EndFeet.Y <= allowedLag &&
                    (!paced || PacedEndReaches(output.EndFeet.Y, final.Height, row.Feet.Y, Budget(row),
                        RoofLimit(new(output.EndFeet.X, output.EndFeet.Z), final.Height)));
            }
            return output with
            {
                GeometryMatchesOracle = readsMatch && placed,
                GeometryPassed = readsMatch && placed && output.GeometryPassed,
                Paced = paced,
            };
        }
        return output with { GeometryMatchesOracle = output.GeometryPassed };
    }

    /// <summary>A rise from the row's feet to the support where the body ended that one tick's climb budget cannot
    /// pay is a paced climb. Its feet may trail that support by up to the legs band, but must reach the lowest of the
    /// support, the budgeted height and the roof limit.</summary>
    internal static bool Paced(SupportCostRow row, double supportHeight) => supportHeight - row.Feet.Y > Budget(row);

    static double Budget(SupportCostRow row) => (double)SupportGateCostScenes.Tuning.MaxStepClimbSpeed * row.Dt;

    /// <summary>A paced end reaches the lowest of its support, the budgeted height and the roof limit, less the
    /// 0.001 placement allowance.</summary>
    internal static bool PacedEndReaches(double endFeet, double support, double startFeet, double budget,
        double? roofLimit) =>
        endFeet >= Math.Min(Math.Min(support, startFeet + budget), roofLimit ?? double.PositiveInfinity) - 0.001;

    /// <summary>The highest feet the cost tuning's capsule reaches under the scene's roof over the footprint disc at
    /// <paramref name="axis"/>, or null when nothing is overhead.</summary>
    internal double? RoofLimit(Vector2 axis, double supportHeight) =>
        SupportCostRoofOracle.Limit(scene, axis, row.Query.FootRadius, supportHeight, SupportGateCostScenes.Tuning);

    /// <summary>The installed scene, for a test that adds geometry the oracles then read.</summary>
    internal SupportFootprintScene Scene => scene;

    EnvironmentAcquisitionFixture Environment()
    {
        var identity = new MovementQueryIdentity("closure", 1, "scope");
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        var scope = new MovementQueryScope(new(-1500, -30, -30), new(1500, 30, 30), 20, 20, Room, identity, frame);
        var producer = new EnvironmentAcquisitionFixture(View, scope) { ContactSupportEnabled = true };
        producer.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            ground = row.Analytic ? new(new(request.Body.Centre.X, request.Body.Centre.Z), row.HeightAt(request.Body.Centre.X),
                row.NormalAt(request.Body.Centre.X), new("world", "floor"), Room, null, identity) : null;
            return MovementAvailability.Known;
        };
        producer.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
        {
            binding = new(new("world", "floor"), Room, null, identity);
            return MovementAvailability.Known;
        };
        producer.OnBodySample = SampleBody;
        producer.OnCoverage = (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            double enter = 0, exit = 1;
            Vector3 contactNormal = Vector3.UnitY;
            double radial = query.Body.Radius;
            bool wet = !float.IsNaN(row.WaterSurface) &&
                Clip(query.Body.Centre.Y, query.Delta.Y, -10 - query.Body.HalfHeight,
                    row.WaterSurface + query.Body.HalfHeight, -Vector3.UnitY,
                    ref enter, ref exit, ref contactNormal) &&
                Clip(query.Body.Centre.X, query.Delta.X, row.WaterMinX - radial,
                    double.PositiveInfinity, -Vector3.UnitX, ref enter, ref exit, ref contactNormal);
            if (!wet)
            {
                spans[0] = new(0, 1, 0, 0, true);
                return new(MovementAvailability.Known, 1, 1, 0, 0, 0, identity);
            }
            float a = (float)enter, b = (float)exit;
            Vector3 at = query.Body.Centre + query.Delta * a;
            var interval = new MovementWaterInterval(-10, row.WaterSurface, row.WaterSurface, true, "bottom", "top");
            contacts[0] = new(new("world", "water"), Room, interval,
                new(MathF.Max(at.X, row.WaterMinX), at.Z), contactNormal, a, "shore", 1,
                MovementContactOverlap.Overlapping, MovementContactOverlap.Overlapping);
            int written = 0;
            if (a > 0) spans[written++] = new(0, a, 0, 0, true);
            spans[written++] = new(a, b, 0, 1, false);
            if (b < 1) spans[written++] = new(b, 1, 1, 0, true);
            return new(MovementAvailability.Known, written, written, 1, 1, 0, identity);
        };
        return producer;
    }

    internal MovementWaterPoint SampleBody(in MovementBodyQuery body)
    {
        Vector3 feet = body.Feet;
        bool wet = !float.IsNaN(row.WaterSurface) && feet.X >= row.WaterMinX &&
            feet.Y >= -10 && feet.Y <= row.WaterSurface;
        return new(MovementAvailability.Known, Room,
            wet ? new MovementDomainKey("world", "water") : null, wet, 1,
            wet ? new MovementWaterInterval(-10, row.WaterSurface, row.WaterSurface, true, "bottom", "top") : null);
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

    static string SupportOutcome(SupportStatus status) => status switch
    {
        SupportStatus.Walkable => "Walkable",
        SupportStatus.Steep => "Steep",
        SupportStatus.Refused => "Refused",
        _ => "None",
    };
    static string GroundOutcome(GroundFooting footing) => footing switch
    {
        GroundFooting.Walkable => "Walkable",
        GroundFooting.Steep => "Steep",
        GroundFooting.Held => "Held",
        _ => "None",
    };
    public void Dispose() { movementBorrowed?.Dispose(); borrowed?.Dispose(); inner.Dispose(); scene.Dispose(); }
}

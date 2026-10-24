using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationInitialHoldReuseTests(ITestOutputHelper output)
{
    [Fact]
    public void ParallelStableProvidersShareOneInitialHoldPerSource()
    {
        using var fixture = new ContactNavigationReuseFixture();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            byte[] referenceFile;
            using (ContactNavigationBuildHooks.Install(new() { DisableInitialHoldReuse = true }))
                referenceFile = fixture.Build(new(2));
            long referenceEdgeSteps = SupportQueryDiagnostics.Snapshot().EdgeSteps;
            SupportQueryDiagnostics.Reset();
            byte[] parallelFile = fixture.Build(new(2));
            SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(referenceFile, parallelFile);
            Assert.True(counts.InitialHoldReuseHits > 0);
            Assert.Equal(referenceEdgeSteps - counts.InitialHoldReuseHits, counts.EdgeSteps);
            output.WriteLine($"holds={counts.InitialHoldReuseHits} edgeSteps={counts.EdgeSteps} reference={referenceEdgeSteps}");
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void StableSerialOptInMatchesParallelAndKeepsOneRealHold()
    {
        using var fixture = new ContactNavigationReuseFixture();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            byte[] reference;
            using (ContactNavigationBuildHooks.Install(new() { DisableFootprintReuse = true, DisableInitialHoldReuse = true }))
                reference = fixture.Build();
            var off = SupportQueryDiagnostics.Snapshot();
            SupportQueryDiagnostics.Reset();
            Assert.Equal(reference, fixture.Build(new(1)));
            var footprint = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(0, footprint.InitialHoldReuseHits);
            Assert.Equal(off.EdgeSteps, footprint.EdgeSteps);
            foreach (int degree in new[] { 1, 2 })
            {
                SupportQueryDiagnostics.Reset();
                Assert.Equal(reference, fixture.Build(new(degree) { UseStableContactHoldReuse = true }));
                var combined = SupportQueryDiagnostics.Snapshot();
                Assert.True(combined.InitialHoldReuseHits > 0);
                Assert.Equal(off.EdgeSteps - combined.InitialHoldReuseHits, combined.EdgeSteps);
                Assert.True(combined.Find < footprint.Find);
            }
            using var proof = fixture.Proof(holds: true);
            using var source = proof.BeginSource(ContactNavigationReuseFixture.Source());
            fixture.StepInputs.Clear();
            Assert.True(proof.TryEdge(fixture.From, fixture.To));
            int bindings = fixture.Trace.OfType<ContactNavigationReuseFixture.BindingCall>().Count();
            Assert.True(proof.TryEdge(fixture.From, fixture.To));
            Assert.True(fixture.Trace.OfType<ContactNavigationReuseFixture.BindingCall>().Count() > bindings);
            Assert.NotEmpty(fixture.StepInputs);
            Assert.All(fixture.StepInputs, input => Assert.Null(input.Selection));
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SerialWithoutAssertionPreservesStatefulCallbackTrace(bool refuses)
    {
        using var fixture = new ContactNavigationReuseFixture();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        int calls = 0;
        fixture.OnHeight = (_, _) =>
        {
            fixture.HeightValue = ++calls % 2 == 0 ? 0 : refuses ? 0.00001f : -0f;
            fixture.Trace.Enqueue(("stateful", calls, BitConverter.SingleToInt32Bits(fixture.HeightValue)));
        };
        try
        {
            byte[]? reference = null, actual = null;
            Exception? originalFailure = Record.Exception(() => reference = fixture.Build());
            Assert.Equal(refuses, originalFailure is not null);
            object[] trace = fixture.Trace.ToArray();
            fixture.Trace.Clear();
            calls = 0;
            SupportQueryDiagnostics.Reset();
            Exception? failure = Record.Exception(() => actual = fixture.Build(new(1)));
            Assert.Equal(originalFailure?.GetType(), failure?.GetType());
            Assert.Equal(originalFailure?.Message, failure?.Message);
            Assert.Equal(reference, actual);
            Assert.Equal(trace, fixture.Trace.ToArray());
            Assert.Equal(0, SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits);
            Assert.False(new NavBakeExecution(1).UseStableContactHoldReuse);
            fixture.OnHeight = null;
            fixture.HeightValue = 0;
            var legacy = Tuning with { Controller = MovementControllerKind.Legacy, Water = default };
            var a = fixture.Bake.BuildProfile(legacy, default, GroundProfileOptions.Default, null);
            var b = fixture.Bake.BuildProfile(legacy, default, GroundProfileOptions.Default,
                new(1) { UseStableContactHoldReuse = true });
            SameGraphBits(a, b);
            Assert.Equal(0, SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReusedHoldAuthenticatesAnImmediateArrivalAndKeepsItsSlice(int budget)
    {
        using var fixture = new ContactNavigationReuseFixture(maxSteps: budget);
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            using var reference = fixture.Proof(false);
            using var proof = fixture.Proof(false, true);
            using var group = proof.BeginSource(ContactNavigationReuseFixture.Source());
            Vector3 from = Vector3.Zero;
            Assert.True(proof.TryEdge(from, from));
            foreach (float distance in new[] { 0f, MathF.BitDecrement(0.001f), 0.001f, MathF.BitIncrement(0.001f), 0.02f, 0.25f })
            {
                Vector3 to = new(distance, 0, 0);
                bool expected = distance <= 0.001f || budget == 2 && distance < 0.03f;
                Assert.Equal(expected, reference.TryEdge(from, to));
                Assert.Equal(expected, proof.TryEdge(from, to));
            }
            Vector3 outsideBall = new(0.0008f, 0.0008f, 0);
            Assert.Equal(budget == 2, reference.TryEdge(from, outsideBall));
            Assert.Equal(budget == 2, proof.TryEdge(from, outsideBall));
            Assert.True(SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits > 0);
            fixture.Water.Advance();
            string original = Assert.Throws<InvalidOperationException>(() => reference.TryEdge(from, from)).Message;
            Assert.Equal(original, Assert.Throws<InvalidOperationException>(() => proof.TryEdge(from, from)).Message);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void FirstRejectedHoldIsNeverRetained()
    {
        using var fixture = new ContactNavigationReuseFixture();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            using var proof = fixture.Proof(holds: true);
            using var group = proof.BeginSource(ContactNavigationReuseFixture.Source());
            Vector3 unsupported = fixture.From + Vector3.UnitY * 0.8f;
            Assert.False(proof.TryEdge(unsupported, unsupported));
            Assert.False(proof.TryEdge(unsupported, unsupported));
            Assert.Equal(0, SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits);
            Assert.Equal(2, SupportQueryDiagnostics.Snapshot().EdgeSteps);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void HoldEligibilityIsIndependentOfFootprintCapability()
    {
        using var fixture = new ContactNavigationReuseFixture(unsupported: true);
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            using var proof = fixture.Proof(holds: true);
            using var group = proof.BeginSource(ContactNavigationReuseFixture.Source());
            Assert.True(proof.TryEdge(fixture.From, fixture.From));
            Assert.True(proof.TryEdge(fixture.From, fixture.From));
            Assert.Equal(1, SupportQueryDiagnostics.Snapshot().EdgeSteps);
            Assert.Equal(1, SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits);
            Assert.Equal(0, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
            ContactNavigationReuseFixture.Released();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("identity")]
    [InlineData("extent")]
    [InlineData("signed-zero-extent")]
    public void WaterBasisChangesCannotReuseAnImmediateArrival(string change)
    {
        using var fixture = new ContactNavigationReuseFixture();
        fixture.Water.ExtentOverride = new(Vector3.Zero, Vector3.One);
        using var proof = fixture.Proof(false, true);
        using var group = proof.BeginSource(ContactNavigationReuseFixture.Source());
        Assert.True(proof.TryEdge(fixture.From, fixture.From));
        switch (change)
        {
            case "generation": fixture.Water.Advance(); break;
            case "identity": fixture.Water.IdentityOverride = "other"; break;
            case "extent": fixture.Water.ExtentOverride = new(Vector3.Zero, new(2, 1, 1)); break;
            default: fixture.Water.ExtentOverride = new(new(-0f, 0, 0), Vector3.One); break;
        }
        Assert.Throws<InvalidOperationException>(() => proof.TryEdge(fixture.From, fixture.From));
    }

    public static IEnumerable<object[]> PinnedDegrees => ContactNavigationBaselineTests.Baselines
        .SelectMany(row => new[] { 1, 3 }.Select(degree => new object[] { row[0], row[1], degree }));

    [Theory]
    [MemberData(nameof(PinnedDegrees))]
    public void StableHoldBaselinesAtBothDegrees(string fixture, string hash, int degree)
    {
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            ContactNavigationBaselineTests.BaselineBake reference;
            using (ContactNavigationBuildHooks.Install(new() { DisableInitialHoldReuse = true }))
                reference = ContactNavigationBaselineTests.BuildFixture(fixture, new(degree));
            var off = SupportQueryDiagnostics.Snapshot();
            SupportQueryDiagnostics.Reset();
            var actual = ContactNavigationBaselineTests.BuildFixture(fixture,
                new(degree) { UseStableContactHoldReuse = true });
            var counts = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(GroundNavigationBakeRoundTripTests.Write(reference.Bake), GroundNavigationBakeRoundTripTests.Write(actual.Bake));
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(ContactNavigationBaselineTests.NormalizeFile(actual))));
            ContactNavigationBaselineTests.AssertFixture(fixture, actual);
            Assert.True(counts.InitialHoldReuseHits > 0);
            Assert.Equal(off.EdgeSteps - counts.InitialHoldReuseHits, counts.EdgeSteps);
            ContactNavigationReuseFixture.Released();
            output.WriteLine($"{fixture} degree={degree} hash={hash} holds={counts.InitialHoldReuseHits} " +
                $"edgeSteps={counts.EdgeSteps} reference={off.EdgeSteps}");
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MutationAfterLastHitRefusesSerialAndParallelPublication(int degree)
    {
        using var fixture = new ContactNavigationReuseFixture();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            if (degree > 1)
            {
                using var scene = new SmallStairScene(water: fixture.Water);
                int bands = ContactNavigationWorkers.BandCount(degree, scene.Build(null).Space.Links.Count);
                int completed = 0;
                SupportQueryDiagnostics.Reset();
                using var hooks = ContactNavigationBuildHooks.Install(new()
                {
                    BandCompleted = (phase, _) =>
                    {
                        if (phase == ContactNavigationPhase.Links && Interlocked.Increment(ref completed) == bands)
                            fixture.Water.Advance();
                    },
                });
                var refused = Assert.Throws<InvalidOperationException>(() => scene.Build(new(degree)));
                Assert.Contains("snapshot changed", refused.Message);
                Assert.True(SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits > 0);
                return;
            }
            fixture.Water.GenerationReads = 0;
            fixture.Build(new(1) { UseStableContactHoldReuse = true });
            int finalRead = fixture.Water.GenerationReads;
            fixture.Water.GenerationReads = 0;
            SupportQueryDiagnostics.Reset();
            // A deterministic read ordinal changes water on the final publication authentication.
            fixture.Water.BeforeGenerationRead = () =>
            {
                if (fixture.Water.GenerationReads == finalRead) fixture.Water.Advance();
            };
            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Build(new(1) { UseStableContactHoldReuse = true }));
            Assert.Contains("snapshot changed", failure.Message);
            Assert.True(SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits > 0);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void EveryHoldKeyDifferenceMisses()
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var otherWorld = new ContactNavigationReuseFixture();
        using var binding = fixture.Context.CreateNavigationBinding(Tuning);
        var frame = fixture.Context.NavigationFrame;
        var scope = binding.CreateOrdinaryScope(frame);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(scope, out var lease));
        using var queries = lease!;
        var initial = new FramedMovementState(new() { Grounded = true, SpeedScale = 1 }, frame, null);
        object profile = new(), provider = new();
        var source = ContactNavigationReuseFixture.Source();
        ContactNavigationHoldKey Key(FramedMovementState? state = null, MoveTuning? tuning = null,
            MovementQueryScope? region = null, MovementQueryLeaseId? basis = null, float dt = 1f / 30,
            int steps = 160, object? p = null, ContactNavigationSource? s = null, object? b = null) =>
            new(state ?? initial, tuning ?? Tuning, region ?? scope, basis ?? queries.Id, dt, steps,
                p ?? profile, s ?? source, b ?? provider);
        var memo = new ContactNavigationInitialHold();
        var key = Key();
        memo.Remember(key, initial.State, frame, Vector3.Zero, MovementStepOutcome.Advanced);
        Assert.True(memo.TryGet(Key(), out _, out _, out _, out _));
        var changed = new List<ContactNavigationHoldKey>
        {
            Key(dt: MathF.BitIncrement(1f / 30)), Key(steps: 159), Key(p: new()), Key(b: new()),
            Key(s: source with { Layer = 1 }), Key(s: source with { Raw = source.Raw with { SurfaceOrdinal = 1 } }),
            Key(s: source with { Raw = source.Raw with { ColumnX = 7 } }),
            Key(s: source with { Raw = source.Raw with { ColumnZ = 5 } }),
            Key(s: source with { Raw = source.Raw with { RawHeightBits = 1 } }),
            Key(state: new(initial.State, new(frame.Frame, frame.PhysicsOrigin, frame.Epoch + 1), null)),
            Key(state: new(initial.State, new(new(1, 0), frame.PhysicsOrigin, frame.Epoch), null)),
            Key(state: new(initial.State, new(new(0, 1), frame.PhysicsOrigin, frame.Epoch), null)),
            Key(state: new(initial.State, new(frame.Frame, new(-0f, 0, 0), frame.Epoch), null)),
            Key(state: new(initial.State, new(frame.Frame, new(0, -0f, 0), frame.Epoch), null)),
            Key(state: new(initial.State, new(frame.Frame, new(0, 0, -0f), frame.Epoch), null)),
            Key(region: new(scope.Min, scope.Max, scope.MaxRise, scope.MaxDrop + 1,
                scope.WorldId, scope.CurrentSpace, scope.Identity, frame)),
            Key(tuning: Tuning with { Water = new(WaterTraversalMode.WadeOnly) }),
            Key(tuning: Tuning with { Water = new(Tuning.Water.Mode, 1) }),
            Key(tuning: Tuning with { Water = new(Tuning.Water.Mode, -0f) }),
            Key(basis: new(queries.Id.SourceWorld, queries.Id.GeometryGeneration + 1,
                queries.Id.EnvironmentGeneration, queries.Id.FrameEpoch)),
            Key(basis: new(queries.Id.SourceWorld, queries.Id.GeometryGeneration,
                queries.Id.EnvironmentGeneration + 1, queries.Id.FrameEpoch)),
            Key(basis: new(queries.Id.SourceWorld, queries.Id.GeometryGeneration,
                queries.Id.EnvironmentGeneration, queries.Id.FrameEpoch + 1)),
            Key(basis: new(otherWorld.Scene.Physics, queries.Id.GeometryGeneration,
                queries.Id.EnvironmentGeneration, queries.Id.FrameEpoch)),
        };
        foreach (var region in new[]
        {
            new MovementQueryScope(scope.Min - Vector3.One, scope.Max, scope.MaxRise, scope.MaxDrop,
                scope.WorldId, scope.CurrentSpace, scope.Identity, frame),
            new MovementQueryScope(scope.Min, scope.Max + Vector3.One, scope.MaxRise, scope.MaxDrop,
                scope.WorldId, scope.CurrentSpace, scope.Identity, frame),
            new MovementQueryScope(scope.Min, scope.Max, scope.MaxRise + 1, scope.MaxDrop,
                scope.WorldId, scope.CurrentSpace, scope.Identity, frame),
            new MovementQueryScope(scope.Min, scope.Max, scope.MaxRise, scope.MaxDrop,
                "other", null, scope.Identity, frame),
            new MovementQueryScope(scope.Min, scope.Max, scope.MaxRise, scope.MaxDrop,
                scope.WorldId, new(scope.WorldId, "other"), scope.Identity, frame),
            new MovementQueryScope(scope.Min, scope.Max, scope.MaxRise, scope.MaxDrop,
                scope.WorldId, null, new("other", 1, scope.Identity.ScopeDigest), frame),
            new MovementQueryScope(scope.Min, scope.Max, scope.MaxRise, scope.MaxDrop,
                scope.WorldId, null, new(scope.Identity.ClosureId, scope.Identity.PolicyVersion + 1, scope.Identity.ScopeDigest), frame),
            new MovementQueryScope(scope.Min, scope.Max, scope.MaxRise, scope.MaxDrop,
                scope.WorldId, null, new(scope.Identity.ClosureId, scope.Identity.PolicyVersion, "other"), frame),
            new MovementQueryScope(scope.Min, scope.Max, scope.MaxRise, scope.MaxDrop,
                scope.WorldId, null, scope.Identity, new(frame.Frame, frame.PhysicsOrigin, frame.Epoch + 1)),
        }) changed.Add(Key(region: region));
        foreach (FieldInfo field in typeof(MoveState).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object boxed = initial.State;
            field.SetValue(boxed, Different(field.GetValue(boxed)!));
            changed.Add(Key(state: new((MoveState)boxed, frame, null)));
        }
        foreach (PropertyInfo property in typeof(MoveTuning).GetProperties().Where(p => p.SetMethod is not null))
        {
            if (property.PropertyType == typeof(WaterTraversalPolicy)) continue;
            object boxed = Tuning;
            property.SetValue(boxed, Different(property.GetValue(boxed)!));
            changed.Add(Key(tuning: (MoveTuning)boxed));
        }
        foreach (PropertyInfo property in typeof(MovementCommitment).GetProperties().Where(p => p.SetMethod is not null))
        {
            object boxed = initial.State.Commitment;
            property.SetValue(boxed, Different(property.GetValue(boxed)!));
            var state = initial.State;
            state.Commitment = (MovementCommitment)boxed;
            changed.Add(Key(state: new(state, frame, null)));
        }
        Assert.All(changed, miss => Assert.False(memo.TryGet(miss, out _, out _, out _, out _)));
        memo.Reset();
        Assert.False(memo.TryGet(key, out _, out _, out _, out _));
    }

    [Fact]
    public void SuccessfulHoldCopiesEveryNumericStateBitWithoutSelection()
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var binding = fixture.Context.CreateNavigationBinding(Tuning);
        var frame = fixture.Context.NavigationFrame;
        var scope = binding.CreateOrdinaryScope(frame);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(scope, out var lease));
        using var queries = lease!;
        object numeric = new MoveState();
        foreach (var field in typeof(MoveState).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            field.SetValue(numeric, Different(field.GetValue(numeric)!));
        var state = (MoveState)numeric;
        var key = new ContactNavigationHoldKey(new(state, frame, null), Tuning, scope, queries.Id,
            1f / 30, 160, this, ContactNavigationReuseFixture.Source(), fixture);
        var memo = new ContactNavigationInitialHold();
        var savedFrame = new MovementFrameDescriptor(new(3, 4), new(-0f, 2, 3), 7);
        Vector3 savedFeet = new(-0f, 1, -2);
        memo.Remember(key, state, savedFrame, savedFeet, MovementStepOutcome.Advanced);
        Assert.True(memo.TryGet(key, out var copy, out var copiedFrame, out var feet, out var outcome));
        Assert.Equal(NumericBits(savedFrame), NumericBits(copiedFrame));
        Assert.Equal(NumericBits(savedFeet), NumericBits(feet));
        Assert.Equal(MovementStepOutcome.Advanced, outcome);
        Assert.Equal(NumericBits(state), NumericBits(copy));
    }

    static object Different(object value) => value switch
    {
        float f => f == 0 ? -0f : MathF.BitIncrement(f == float.PositiveInfinity ? 1 : f),
        bool b => !b,
        Vector2 v => v + Vector2.One,
        Vector3 v => v + Vector3.One,
        MovementControllerKind => MovementControllerKind.Legacy,
        MovementCommitmentPhase => MovementCommitmentPhase.Preparing,
        MovementCommitmentEndReason => MovementCommitmentEndReason.Landed,
        uint => 1u,
        WaterExcursionState => WaterExcursionState.Surface,
        MovementCommitment => new MovementCommitment(1, Vector2.UnitX, 2, 3, 4, 0.5f, 0.25f, 5),
        _ => throw new InvalidOperationException(value.GetType().FullName),
    };

    static IEnumerable<object> NumericBits(object value) => value.GetType()
        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(f => !f.IsStatic)
        .SelectMany(field => field.GetValue(value) switch
        {
            float f => new object[] { BitConverter.SingleToInt32Bits(f) },
            { } nested when nested.GetType().IsValueType && !nested.GetType().IsPrimitive && !nested.GetType().IsEnum => NumericBits(nested),
            { } scalar => new[] { scalar },
            _ => throw new InvalidOperationException(),
        });
}

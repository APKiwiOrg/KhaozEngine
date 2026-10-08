using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public class MovementEnvironmentReconstructionTests
{
    [Fact]
    public void CorrectionRebuildsTheLeftRoomInsteadOfTheDiscardedRightPrediction()
    {
        using var scene = Scene();
        using var lease = scene.Acquire(null);
        MovementScopeWitness witness = lease.Witness;
        FramedMovementState corrected = State(lease, -1f, "right");
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(corrected, out var selection));
        Assert.Equal(Space("left"), selection.Space);
        Assert.Null(selection.Support);
        MovementBodyQuery body = Body(corrected, selection);
        Assert.Equal(corrected.State.Position, body.Centre);
        MovementWaterPoint point = lease.SampleCentreWater(body);
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.Equal(Space("left"), point.Space);
        Assert.False(point.InWater);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        MovementCoverageResult trace = lease.TraceWater(new(body, Vector3.Zero), spans, contacts);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.Equal(0, trace.ContactsWritten);
        Assert.All(spans[..trace.SpansWritten], span => Assert.True(span.HasDryCoverage));
        Assert.Same(witness, lease.Witness);
        Assert.Null(witness.Scope.CurrentSpace);
        Assert.Equal(1, scene.Acquisition.RebuildCalls);
    }

    [Fact]
    public void ExactPortalPlaneReconstructsItsHalfOpenOwnerAndMixedFootprint()
    {
        using var scene = Scene();
        using var lease = scene.Acquire(null);
        FramedMovementState corrected = State(lease, 0f, "left");
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(corrected, out var selection));
        Assert.Equal(Space("right"), selection.Space);
        MovementBodyQuery body = Body(corrected, selection);
        Assert.Equal(corrected.State.Position, body.Centre);
        MovementWaterPoint point = lease.SampleCentreWater(body);
        Assert.True(point.InWater);
        Assert.Equal(Domain("right-water"), point.Domain);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        MovementCoverageResult trace = lease.TraceWater(new(body, Vector3.Zero), spans, contacts);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.All(spans[..trace.SpansWritten], span => { Assert.True(span.HasDryCoverage); Assert.Equal(1, span.ContactCount); });
        Assert.All(contacts[..trace.ContactsWritten], contact => Assert.Equal(Space("right"), contact.Space));
        Assert.Null(lease.Witness.Scope.CurrentSpace);
    }

    [Fact]
    public void FailedCorrectedGeometryCannotKeepEarlierSelectionReady()
    {
        using var scene = Scene();
        using var lease = scene.Acquire(null);
        MovementScopeWitness witness = lease.Witness;
        FramedMovementState predicted = State(lease, 1f, "left");
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(predicted, out var oldSelection));
        MovementBodyQuery oldBody = Body(predicted, oldSelection);
        Assert.Equal(predicted.State.Position, oldBody.Centre);
        Assert.True(lease.SampleCentreWater(oldBody).InWater);

        Assert.Equal(MovementAvailability.Unresolved, lease.RebuildSelection(State(lease, 10f, "right"), out var refused));
        Assert.Equal(default(MovementSelection), refused);
        Assert.Equal(MovementAvailability.Unresolved, lease.SampleCentreWater(oldBody).Availability);
        var request = new MovementSupportRequest(oldBody, 0f, 0f, 0.8f, new MovementTransitionContext(oldBody.CurrentSpace, oldBody.Feet));
        MovementSupportCandidate candidate = new(new MovementSupportKey("world", "sentinel"), Space("right"),
            Vector3.Zero, Vector3.UnitY, null);
        MovementSupportCandidate[] candidates = [candidate];
        Assert.Equal(MovementAvailability.Unresolved, lease.EnumerateSupport(request, candidates).Availability);
        Assert.Equal(candidate, candidates[0]);
        MovementCoverageSpan span = new(0.5f, 1f, 0, 1, true);
        MovementDomainContact contact = new(Domain("sentinel"), Space("right"), new(-4f, 3f, 3f, true, "low", "high"),
            Vector2.One, Vector3.UnitZ, 0.75f, "sentinel", 123u, MovementContactOverlap.Overlapping, MovementContactOverlap.Overlapping);
        MovementCoverageSpan[] spans = [span];
        MovementDomainContact[] contacts = [contact];
        Assert.Equal(MovementAvailability.Unresolved, lease.TraceWater(new(oldBody, Vector3.Zero), spans, contacts).Availability);
        Assert.Equal(span, spans[0]);
        Assert.Equal(contact, contacts[0]);
        Assert.Equal(1, scene.Acquisition.SampleCalls);
        Assert.Equal(0, scene.Acquisition.SupportCalls);
        Assert.Equal(0, scene.Acquisition.CoverageCalls);

        FramedMovementState restored = State(lease, -1f, "right");
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(restored, out var selection));
        Assert.Equal(Space("left"), selection.Space);
        MovementBodyQuery restoredBody = Body(restored, selection);
        Assert.Equal(restored.State.Position, restoredBody.Centre);
        MovementWaterPoint restoredPoint = lease.SampleCentreWater(restoredBody);
        Assert.Equal(MovementAvailability.Known, restoredPoint.Availability);
        Assert.False(restoredPoint.InWater);
        Assert.Same(witness, lease.Witness);
        Assert.Null(witness.Scope.CurrentSpace);
        Assert.Equal(3, scene.Acquisition.RebuildCalls);
    }

    static FramedMovementState State(MovementQueryLease lease, float x, string discarded) =>
        new(new MoveState { Position = new Vector3(x, 0.5f, 0f), Grounded = false }, lease.Frame,
            new MovementSelection(Space(discarded), null, Identity));

    static MovementBodyQuery Body(FramedMovementState state, MovementSelection selection) =>
        new(state.State.Position, 0.25f, 0.75f, selection.Space, null);

    static AnalyticMovementEnvironment Scene()
    {
        var left = new AnalyticBox(new Vector3(-4f), new Vector3(0f, 4f, 4f));
        var right = new AnalyticBox(new Vector3(0f, -4f, -4f), new Vector3(4f));
        var water = new AnalyticBox(new Vector3(0f, -4f, -4f), new Vector3(4f, 3f, 4f));
        var scene = new AnalyticMovementEnvironment([new Room("left", left), new Room("right", right)],
            [new Water("right-water", "right", water, 3f)], [new Link("left", "right")]);
        scene.Slab(-4f);
        return scene;
    }
}

using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementSupportSelectionTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementSupportKey Low = new("world", "low");
    static readonly MovementSupportKey High = new("world", "high");

    [Fact]
    public void HigherEligibleSupportWinsOverTheCurrentLowerOwner()
    {
        using var scene = new Scene();
        scene.Floor(0f);
        scene.Floor(2f);
        scene.Candidates = [Candidate(Low, 0f), Candidate(High, 2f)];
        Capture result = scene.Select(rise: 3f, current: Low);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(High, result.Candidate!.Value.Owner);
        Assert.InRange(result.Centre.Y, 2.75f, 2.752f);
    }

    [Fact]
    public void ClearanceIsAppliedBeforeChoosingTheHighestSupport()
    {
        using var scene = new Scene();
        scene.Floor(0f);
        scene.Floor(2f);
        scene.World.AddStatic(new BoxShape(new Vector3(3f, 0.1f, 3f)), Pose.At(new Vector3(0f, 2.9f, 0f)));
        scene.Candidates = [Candidate(Low, 0f), Candidate(High, 2f)];
        Capture result = scene.Select(rise: 3f);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(Low, result.Candidate!.Value.Owner);
    }

    [Fact]
    public void IndependentEqualHeightOwnersAreAmbiguousEvenWhenOneIsCurrent()
    {
        using var scene = new Scene();
        scene.Floor(0f);
        scene.Candidates = [Candidate(Low, 0f), Candidate(High, 0f)];
        Capture result = scene.Select(current: Low);
        Assert.Equal(MovementAvailability.Invalid, result.Availability);
        Assert.Null(result.Candidate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalSeamAliasesDoNotCreateAnIndependentOwnerTie(bool reverse)
    {
        using var scene = new Scene();
        scene.Floor(0f);
        var a = Candidate(Low, 0f);
        var b = new MovementSupportCandidate(Low, Room, Vector3.Zero, Vector3.UnitY, "declared-seam");
        scene.Candidates = reverse ? [b, a] : [a, b];
        Capture result = scene.Select(current: Low);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(Low, result.Candidate!.Value.Owner);
        Assert.InRange(result.Centre.Y, 0.75f, 0.752f);
    }

    [Fact]
    public void AnotherSpaceWithoutLegalLinkProvenanceCannotBecomeSupport()
    {
        using var scene = new Scene();
        scene.Floor(0f);
        scene.Candidates = [new MovementSupportCandidate(High, new MovementSpaceKey("world", "other-room"),
            Vector3.Zero, Vector3.UnitY, null)];
        Capture result = scene.Select();
        Assert.Equal(MovementAvailability.Invalid, result.Availability);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void AProducerCertifiedPortalCandidateCanBeSelected()
    {
        using var scene = new Scene();
        scene.Floor(0f);
        scene.Candidates = [new MovementSupportCandidate(High, new MovementSpaceKey("world", "other-room"),
            Vector3.Zero, Vector3.UnitY, "portal")];
        Capture result = scene.Select();
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(High, result.Candidate!.Value.Owner);
    }

    [Fact]
    public void ARealSideWallCannotBeHiddenByTheFloorContact()
    {
        using var scene = new Scene();
        scene.Floor(0f);
        scene.World.AddStatic(new BoxShape(new Vector3(0.1f, 2f, 3f)), Pose.At(new Vector3(0.3f, 1f, 0f)));
        scene.Candidates = [Candidate(Low, 0f)];
        Capture result = scene.Select();
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void TiltedSupportUsesTheUprightCapsuleInsteadOfPlacingItThroughThePlane()
    {
        using var scene = new Scene();
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6f);
        Vector3 normal = Vector3.Transform(Vector3.UnitY, rotation);
        scene.World.AddStatic(new BoxShape(new Vector3(3f, 0.05f, 3f)), new Pose(-normal * 0.05f, rotation));
        scene.Candidates = [new MovementSupportCandidate(Low, Room, Vector3.Zero, normal, null)];
        Capture result = scene.Select();
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(Low, result.Candidate!.Value.Owner);
        Assert.InRange(result.Centre.Y, 0.797f, 0.799f);
        Assert.Equal(Vector3.Zero, result.Candidate.Value.Feet);
    }

    [Fact]
    public void AProfileForbiddenSlopeIsNotEligible()
    {
        using var scene = new Scene();
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3f);
        Vector3 normal = Vector3.Transform(Vector3.UnitY, rotation);
        scene.World.AddStatic(new BoxShape(new Vector3(3f, 0.05f, 3f)), new Pose(-normal * 0.05f, rotation));
        scene.Candidates = [new MovementSupportCandidate(Low, Room, Vector3.Zero, normal, null)];
        Capture result = scene.Select();
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void MissingPhysicalSupportIsUnresolvedRatherThanAUsableFloatingPlacement()
    {
        using var scene = new Scene();
        scene.Candidates = [Candidate(Low, 0f)];
        Capture result = scene.Select();
        Assert.Equal(MovementAvailability.Unresolved, result.Availability);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void BackendCoverageRefusalIsNotClearSupport()
    {
        using var scene = new Scene();
        Vector3[] vertices = [new(-1f, 0f, -1f), new(1f, 0f, 1f), new(-1f, 0f, 1f)];
        var indices = new int[1025 * 3];
        for (int i = 0; i < 1025; i++) { indices[i * 3 + 1] = 1; indices[i * 3 + 2] = 2; }
        scene.World.AddStatic(new TriangleMeshShape(vertices, indices), Pose.Identity);
        scene.Candidates = [Candidate(Low, 0f)];
        Capture result = scene.Select();
        Assert.Equal(MovementAvailability.Unresolved, result.Availability);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void KnownEmptyCandidatesRemainDistinctFromUnresolvedCandidates()
    {
        using var scene = new Scene();
        Capture result = scene.Select();
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void CandidateCapacityRefusalReturnsNoPlacement()
    {
        using var scene = new Scene();
        scene.Candidates = new MovementSupportCandidate[257];
        Capture result = scene.Select();
        Assert.Equal(MovementAvailability.CapacityExceeded, result.Availability);
        Assert.Null(result.Candidate);
    }

    static MovementSupportCandidate Candidate(MovementSupportKey owner, float y) =>
        new(owner, Room, new Vector3(0f, y, 0f), Vector3.UnitY, null);

    sealed class Scene : IDisposable
    {
        public BepuPhysicsWorld World { get; } = new(Vector3.Zero);
        public MovementSupportCandidate[] Candidates = [];
        public void Floor(float y) => World.AddStatic(new BoxShape(new Vector3(3f, 0.05f, 3f)), Pose.At(new Vector3(0f, y - 0.05f, 0f)));
        public Capture Select(float rise = 0.5f, MovementSupportKey? current = null)
        {
            using var view = World.CreateQueryViewExcludingStatics([]);
            var identity = new MovementQueryIdentity("closure", 1u, "scope");
            var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1ul);
            var scope = new MovementQueryScope(new Vector3(-8f), new Vector3(8f), 3f, 2f, Room, identity, frame);
            var environment = new EnvironmentAcquisitionFixture(view, scope);
            environment.OnSupport = (in MovementSupportRequest _, Span<MovementSupportCandidate> output) =>
            {
                if (Candidates.Length > output.Length)
                    return new MovementSupportSet(MovementAvailability.CapacityExceeded, 0, Candidates.Length, identity);
                Candidates.AsSpan().CopyTo(output);
                return new MovementSupportSet(MovementAvailability.Known, Candidates.Length, Candidates.Length, identity);
            };
            var acquired = environment.Acquire();
            using var lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
            var body = new MovementBodyQuery(new Vector3(0f, 0.75f, 0f), 0.3f, 0.75f, Room, current);
            var request = new MovementSupportRequest(body, rise, 2f, MathF.PI / 4f,
                new MovementTransitionContext(Room, body.Feet));
            return SelectSupport(request, lease);
        }
        public void Dispose() => World.Dispose();
    }

    readonly record struct Capture(MovementAvailability Availability, MovementSupportCandidate? Candidate, Vector3 Centre);
    static Capture SelectSupport(in MovementSupportRequest request, MovementQueryLease lease)
    {
        MovementAvailability availability = MovementSupportResolver.Select(request, lease, out var placement);
        return new Capture(availability, placement?.Candidate, placement?.Centre ?? default);
    }
}

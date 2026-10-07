using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public partial class MovementCapsuleResolverTests
{
    [Fact]
    public void InitialPenetrationDoesNotManufactureARecoveryPath()
    {
        using var scene = new Scene();
        scene.World.AddStatic(new BoxShape(Vector3.One), Pose.At(Body().Centre));
        AssertRefused(scene.Resolve(Body(), Vector3.UnitX), MovementAvailability.Unresolved);
    }

    [Fact]
    public void ExactInitialTangencyRequiresPlacementRecoveryEvenWhenMovingAway()
    {
        using var scene = new Scene();
        scene.World.AddStatic(new BoxShape(new(0.5f)), Pose.At(new(0.75f, 0.752f, 0)));
        AssertRefused(scene.Resolve(Body(), -Vector3.UnitX), MovementAvailability.Unresolved);
    }

    [Fact]
    public void AFailureAfterAProvedPrefixDiscardsTheWholeTentativePath()
    {
        using var scene = new Scene { FailAfterProgress = true };
        scene.WallX();
        Capture result = scene.Resolve(Body(), new(4, 0, 2));
        AssertRefused(result, MovementAvailability.Unresolved);
        Assert.Contains(scene.View.Calls, call => call.Status == CapsuleSweepStatus.Clear && call.Delta != Vector3.Zero);
    }

    [Fact]
    public void UnsupportedSelectedGeometryReturnsNoPolyline()
    {
        using var scene = new Scene();
        scene.World.AddStatic(new SphereShape(0.5f), Pose.At(new(6, 0, 0)));
        AssertRefused(scene.Resolve(Body(), Vector3.UnitX), MovementAvailability.Unresolved);
    }

    [Fact]
    public void UnresolvedColdMembershipCannotReachTheBackend()
    {
        using var scene = new Scene { Cold = true };
        AssertRefused(scene.Resolve(Body(), Vector3.UnitX), MovementAvailability.Unresolved);
        Assert.Empty(scene.View.Calls);
    }

    [Fact]
    public void StaleEnvironmentCannotReturnAnAdvancedEndpoint()
    {
        using var scene = new Scene { Stale = true };
        AssertRefused(scene.Resolve(Body(), Vector3.UnitX), MovementAvailability.Stale);
        Assert.Empty(scene.View.Calls);
    }

    [Fact]
    public void EnvironmentBecomingStaleDuringTheSweepDiscardsItsOutput()
    {
        using var scene = new Scene { StaleDuringSweep = true };
        AssertRefused(scene.Resolve(Body(), Vector3.UnitX), MovementAvailability.Stale);
        Assert.Single(scene.View.Calls);
    }

    [Fact]
    public void OutOfScopeMovementHasNoUsablePrefix()
    {
        using var scene = new Scene();
        AssertRefused(scene.Resolve(Body(), new(9, 0, 0)), MovementAvailability.Unresolved);
    }

    [Fact]
    public void NonfiniteDisplacementIsInvalidBeforeAnyGeometryQuery()
    {
        using var scene = new Scene();
        AssertRefused(scene.Resolve(Body(), new(float.NaN, 0, 0)), MovementAvailability.Invalid);
        Assert.Empty(scene.View.Calls);
    }

    [Fact]
    public void DefaultBodyIsInvalidBeforeAnyGeometryQuery()
    {
        using var scene = new Scene();
        AssertRefused(scene.Resolve(default, Vector3.UnitX), MovementAvailability.Invalid);
        Assert.Empty(scene.View.Calls);
    }

    [Fact]
    public void CallerCapacityRefusesAtomically()
    {
        using var scene = new Scene();
        AssertRefused(scene.Resolve(Body(), Vector3.UnitX, capacity: 0), MovementAvailability.CapacityExceeded);
    }

    [Fact]
    public void ABracketTooWideToLocateTheActiveSetCannotAuthorizeAPlacement()
    {
        using var scene = new Scene { WideBracket = true };
        AssertRefused(scene.Resolve(Body(), new(4, 0, 0)), MovementAvailability.Unresolved);
        Assert.Equal(1, scene.View.NonzeroSweeps);
    }

    [Fact]
    public void AHitWithoutACompleteActiveConstraintSetIsUnresolved()
    {
        using var scene = new Scene { OmitImpactContacts = true };
        scene.WallX();
        AssertRefused(scene.Resolve(Body(), new(4, 0, 2)), MovementAvailability.Unresolved);
    }

    [Fact]
    public void CapacityExhaustedAfterComputingASlidePublishesNoPrefix()
    {
        using var scene = new Scene();
        scene.WallX();
        AssertRefused(scene.Resolve(Body(), new(4, 0, 2), capacity: 1), MovementAvailability.CapacityExceeded);
        Assert.Contains(scene.View.Calls, call => call.Status == CapsuleSweepStatus.Clear && call.Delta != Vector3.Zero);
    }

    [Fact]
    public void EightCorrectionsDoNotBecomeAnUnboundedProjectionLoop()
    {
        using var scene = new Scene { EndlessConstraints = true };
        AssertRefused(scene.Resolve(Body(), new(4, 0, 2)), MovementAvailability.Unresolved);
        Assert.Equal(9, scene.View.NonzeroSweeps);
    }

    static void AssertRefused(Capture capture, MovementAvailability expected)
    {
        Assert.Equal(expected, capture.Status);
        Assert.Equal(0, capture.Count);
        Assert.False(capture.Blocked);
        Assert.All(capture.Path, point => Assert.Equal(Sentinel, point));
    }
}

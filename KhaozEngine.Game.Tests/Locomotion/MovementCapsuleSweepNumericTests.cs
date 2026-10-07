using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Request-representation guards only, independent of any physical sweep certificate.
public class MovementCapsuleSweepNumericTests
{
    [Fact]
    public void HitAtARoundedUpNormCannotExtendBeyondTheActualRequest()
    {
        float rounded = MathF.Sqrt(5f);
        // The exact squared norm is 5. This compares represented values without a sqrt oracle.
        Assert.True((double)rounded * rounded > 5d);
        Capture result = Run(new Vector3(1f, 2f, 0f), new(CapsuleSweepStatus.Hit, rounded, rounded, 0f));
        Refused(result, MovementAvailability.Invalid);
        Assert.Equal(1, result.Calls);
    }

    [Fact]
    public void HitBelowTheExactNormStillFitsTheRequestedPath()
    {
        float rounded = MathF.Sqrt(2f);
        Assert.True((double)rounded * rounded < 2d);
        var declared = new CapsuleSweepResult(CapsuleSweepStatus.Hit, rounded, rounded, 0f);
        Capture result = Run(new Vector3(1f, 1f, 0f), declared);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(declared, result.Result);
    }

    [Fact]
    public void ClearUsesTheFullOriginalVectorAndItsRoundedLengthRepresentation()
    {
        Vector3 delta = new(1f, 2f, 0f);
        float rounded = MathF.Sqrt(5f);
        Assert.True((double)rounded * rounded > 5d);
        var declared = new CapsuleSweepResult(CapsuleSweepStatus.Clear, rounded, null, 0f);
        Capture result = Run(delta, declared);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(declared, result.Result);
        Assert.Equal(delta, result.ObservedDisplacement);
    }

    [Fact]
    public void ClearAtTheFloatBelowItsRequiredRepresentationIsNotAFullRequest()
    {
        float below = MathF.BitDecrement(MathF.Sqrt(5f));
        Refused(Run(new Vector3(1f, 2f, 0f), new(CapsuleSweepStatus.Clear, below, null, 0f)),
            MovementAvailability.Invalid);
    }

    [Fact]
    public void ClearAtTheFloatAboveItsRequiredRepresentationCannotBroadenTheRequest()
    {
        float above = MathF.BitIncrement(MathF.Sqrt(5f));
        Refused(Run(new Vector3(1f, 2f, 0f), new(CapsuleSweepStatus.Clear, above, null, 0f)),
            MovementAvailability.Invalid);
    }

    [Fact]
    public void SignedZeroStillRequiresAStationaryClosedQuery()
    {
        var declared = new CapsuleSweepResult(CapsuleSweepStatus.Clear, -0f, null, 0f);
        Capture result = Run(new Vector3(-0f), declared);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(declared, result.Result);
        Assert.Equal(1, result.Calls);
    }

    [Fact]
    public void CapsuleSkinCrossingTheScopeByOneFloatRefusesBeforeDispatch()
    {
        float displacement = MathF.BitIncrement(3.698f);
        Assert.True((double)displacement + 0.3f + 2d * 0.001f > 4d);
        Capture result = Run(new Vector3(displacement, 0f, 0f),
            new(CapsuleSweepStatus.Clear, displacement, null, 0f));
        Refused(result, MovementAvailability.Unresolved);
        Assert.Equal(0, result.Calls);
    }

    [Fact]
    public void NonfiniteDisplacementRefusesBeforeDispatch()
    {
        Capture result = Run(new Vector3(float.NaN, 0f, 0f),
            new(CapsuleSweepStatus.Clear, 1f, null, 0f));
        Refused(result, MovementAvailability.Invalid);
        Assert.Equal(0, result.Calls);
    }

    static void Refused(Capture result, MovementAvailability availability)
    {
        Assert.Equal(availability, result.Availability);
        Assert.Equal(default, result.Result);
    }

    static Capture Run(Vector3 displacement, CapsuleSweepResult declared)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = new ScriptedSweepQueryView(world.CreateQueryViewExcludingStatics([])) { Result = declared };
        var environment = new EnvironmentAcquisitionFixture(view);
        var acquired = environment.Acquire();
        Assert.Equal(MovementAvailability.Known, acquired.Status);
        using var lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        var body = new MovementBodyQuery(Vector3.Zero, 0.3f, 0.75f, new MovementSpaceKey("world", "room"), null);
        MethodInfo? method = typeof(MovementQueryLease).GetMethod("QuerySolidSweep",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] args = [body, displacement, new CapsuleSweepResult(CapsuleSweepStatus.Clear, 123f, null, 0f)];
        try
        {
            var availability = (MovementAvailability)method.Invoke(lease, args)!;
            return new Capture(availability, (CapsuleSweepResult)args[2]!, view.Calls, view.ObservedDisplacement);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    readonly record struct Capture(MovementAvailability Availability, CapsuleSweepResult Result,
        int Calls, Vector3 ObservedDisplacement);
}

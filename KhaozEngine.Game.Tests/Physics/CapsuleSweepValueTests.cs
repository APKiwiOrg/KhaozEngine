using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Value-contract tests only. A valid value does not prove that a backend has certified its geometry.
public class CapsuleSweepValueTests
{
    [Fact]
    public void DefaultIsAnUnresolvedResultWithoutAUsablePrefix()
    {
        object value = Activator.CreateInstance(ResultType())!;
        Assert.Equal(0, Convert.ToInt32(Property(value, "Status")));
        Assert.Equal(0f, Property(value, "ClearThroughDistance"));
        Assert.Null(Property(value, "ImpactDistance"));
        Assert.Equal(0f, Property(value, "CertifiedErrorMetres"));
        Assert.Equal(false, Property(value, "IsComplete"));
        Assert.Equal(true, Property(value, "IsValid"));
    }

    [Fact]
    public void ClearCarriesTheFullDistanceAndNoImpact()
    {
        object value = Create(1, 2f, null, 0.0005f);
        Assert.Equal(2f, Property(value, "ClearThroughDistance"));
        Assert.Null(Property(value, "ImpactDistance"));
        Assert.Equal(0.0005f, Property(value, "CertifiedErrorMetres"));
        Assert.Equal(true, Property(value, "IsComplete"));
        Assert.Equal(true, Property(value, "IsValid"));
    }

    [Fact]
    public void HitRetainsAnOrderedIntervalAndItsDeclaredError()
    {
        object value = Create(2, 0.4f, 0.4005f, 0.001f);
        Assert.Equal(0.4f, Property(value, "ClearThroughDistance"));
        Assert.Equal(0.4005f, Property(value, "ImpactDistance"));
        Assert.Equal(0.001f, Property(value, "CertifiedErrorMetres"));
        Assert.Equal(true, Property(value, "IsComplete"));
    }

    [Fact]
    public void ZeroHitCarriesNoPositiveClearDistance()
    {
        object value = Create(2, 0f, 0f, 0.001f);
        Assert.Equal(2, Convert.ToInt32(Property(value, "Status")));
        Assert.Equal(0f, Property(value, "ClearThroughDistance"));
        Assert.Equal(0f, Property(value, "ImpactDistance"));
        Assert.Equal(true, Property(value, "IsValid"));
    }

    [Fact]
    public void UnresolvedCannotExposeDistanceImpactOrACertifiedError()
    {
        _ = ResultType();
        Assert.ThrowsAny<ArgumentException>(() => Create(0, 1f, null, 0f));
        Assert.ThrowsAny<ArgumentException>(() => Create(0, 0f, 0f, 0f));
        Assert.ThrowsAny<ArgumentException>(() => Create(0, 0f, null, 0.001f));
    }

    [Fact]
    public void NonfiniteNegativeAndUnknownValuesAreRejected()
    {
        _ = ResultType();
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f })
        {
            Assert.ThrowsAny<ArgumentException>(() => Create(1, invalid, null, 0f));
            Assert.ThrowsAny<ArgumentException>(() => Create(1, 1f, null, invalid));
            Assert.ThrowsAny<ArgumentException>(() => Create(2, 0f, invalid, 0f));
        }
        Assert.ThrowsAny<ArgumentException>(() => Create(99, 0f, null, 0f));
    }

    [Fact]
    public void ClearCannotInventImpactAndHitCannotOmitOrInvertIt()
    {
        _ = ResultType();
        Assert.ThrowsAny<ArgumentException>(() => Create(1, 1f, 1f, 0f));
        Assert.ThrowsAny<ArgumentException>(() => Create(2, 1f, null, 0f));
        Assert.ThrowsAny<ArgumentException>(() => Create(2, 2f, 1f, 0f));
        // Physics can represent a larger declared error. The explicit consumer enforces its own cap.
        Assert.Equal(0.01f, Property(Create(1, 1f, null, 0.01f), "CertifiedErrorMetres"));
    }

    [Fact]
    public void OptionalCapabilityUsesThePoseDisplacementAndExistingFilter()
    {
        Type result = ResultType();
        Type? capability = typeof(IPhysicsWorld).Assembly.GetType("KhaozEngine.Physics.IPhysicsCapsuleSweep");
        Assert.NotNull(capability);
        Assert.True(capability.IsInterface);
        MethodInfo? method = capability.GetMethod("SweepCapsuleCertified");
        Assert.NotNull(method);
        Assert.Equal(result, method.ReturnType);
        ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(CapsuleShape), parameters[0].ParameterType);
        Assert.Equal(typeof(Pose), parameters[1].ParameterType);
        Assert.Equal(typeof(Vector3), parameters[2].ParameterType);
        Assert.Equal(typeof(QueryFilter), parameters[3].ParameterType);
        Assert.True(parameters[3].IsOptional);
    }

    static Type ResultType()
    {
        Type? type = typeof(IPhysicsWorld).Assembly.GetType("KhaozEngine.Physics.CapsuleSweepResult");
        Assert.NotNull(type);
        return type;
    }

    static object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    static object Create(int status, float clearThrough, float? impact, float error)
    {
        Type result = ResultType();
        Type? statusType = typeof(IPhysicsWorld).Assembly.GetType("KhaozEngine.Physics.CapsuleSweepStatus");
        Assert.NotNull(statusType);
        ConstructorInfo? constructor = result.GetConstructor([statusType, typeof(float), typeof(float?), typeof(float)]);
        Assert.NotNull(constructor);
        try { return constructor.Invoke([Enum.ToObject(statusType, status), clearThrough, impact, error]); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}

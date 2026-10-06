using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Value-contract tests only. A valid value does not prove that a backend has certified its geometry.
public class CapsuleSweepValueTests
{
    [Fact]
    public void DefaultIsAnUnresolvedResultWithoutAUsablePrefix()
    {
        CapsuleSweepResult value = default;
        Assert.Equal(0, (int)value.Status);
        Assert.Equal(0f, value.ClearThroughDistance);
        Assert.Null(value.ImpactDistance);
        Assert.Equal(0f, value.CertifiedErrorMetres);
        Assert.False(value.IsComplete);
        Assert.True(value.IsValid);
    }

    [Fact]
    public void ClearCarriesTheFullDistanceAndNoImpact()
    {
        CapsuleSweepResult value = Create(1, 2f, null, 0.0005f);
        Assert.Equal(2f, value.ClearThroughDistance);
        Assert.Null(value.ImpactDistance);
        Assert.Equal(0.0005f, value.CertifiedErrorMetres);
        Assert.True(value.IsComplete);
        Assert.True(value.IsValid);
    }

    [Fact]
    public void HitRetainsAnOrderedIntervalAndItsDeclaredError()
    {
        CapsuleSweepResult value = Create(2, 0.4f, 0.4005f, 0.001f);
        Assert.Equal(0.4f, value.ClearThroughDistance);
        Assert.Equal(0.4005f, value.ImpactDistance);
        Assert.Equal(0.001f, value.CertifiedErrorMetres);
        Assert.True(value.IsComplete);
    }

    [Fact]
    public void ZeroHitCarriesNoPositiveClearDistance()
    {
        CapsuleSweepResult value = Create(2, 0f, 0f, 0.001f);
        Assert.Equal(2, (int)value.Status);
        Assert.Equal(0f, value.ClearThroughDistance);
        Assert.Equal(0f, value.ImpactDistance);
        Assert.True(value.IsValid);
    }

    [Fact]
    public void UnresolvedCannotExposeDistanceImpactOrACertifiedError()
    {
        Assert.ThrowsAny<ArgumentException>(() => Create(0, 1f, null, 0f));
        Assert.ThrowsAny<ArgumentException>(() => Create(0, 0f, 0f, 0f));
        Assert.ThrowsAny<ArgumentException>(() => Create(0, 0f, null, 0.001f));
    }

    [Fact]
    public void NonfiniteNegativeAndUnknownValuesAreRejected()
    {
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
        Assert.ThrowsAny<ArgumentException>(() => Create(1, 1f, 1f, 0f));
        Assert.ThrowsAny<ArgumentException>(() => Create(2, 1f, null, 0f));
        Assert.ThrowsAny<ArgumentException>(() => Create(2, 2f, 1f, 0f));
        // Physics can represent a larger declared error. The explicit consumer enforces its own cap.
        Assert.Equal(0.01f, Create(1, 1f, null, 0.01f).CertifiedErrorMetres);
    }

    [Fact]
    public void OptionalCapabilityUsesThePoseDisplacementAndExistingFilter()
    {
        Type result = typeof(CapsuleSweepResult);
        Type capability = typeof(IPhysicsCapsuleSweep);
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

    static CapsuleSweepResult Create(int status, float clearThrough, float? impact, float error) =>
        new((CapsuleSweepStatus)status, clearThrough, impact, error);
}

using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// First contract slice only. No factory or passing value is a backend geometry certificate.
public class CapsuleFeatureContractTests
{
    static Type ResultType => Required("CapsuleFeatureResult");

    [Fact]
    public void DefaultCannotExposeAUsableFeatureOrPartialOutput()
    {
        object result = Activator.CreateInstance(ResultType)!;
        Assert.Equal("Unresolved", Read(result, "Status")?.ToString());
        Assert.Equal(0, Read(result, "Written"));
        Assert.Equal(0, Read(result, "RequiredCapacity"));
        Assert.Null(Read(result, "Lease"));
        Assert.Null(Read(result, "QueryWorld"));
    }

    [Theory]
    [InlineData("Unresolved")]
    [InlineData("NoFeature")]
    [InlineData("Unavailable")]
    [InlineData("Unsupported")]
    [InlineData("Ambiguous")]
    [InlineData("CapacityExceeded")]
    public void RefusalDoesNotCarryAFeatureOrLease(string status)
    {
        object result = Refuse(status, 0);
        Assert.Equal(status, Read(result, "Status")?.ToString());
        Assert.Equal(0, Read(result, "Written"));
        Assert.Null(Read(result, "Lease"));
        Assert.Null(Read(result, "QueryWorld"));
        Assert.Equal(default(Vector3), Read(result, "AxisPoint"));
        Assert.Equal(default(Vector3), Read(result, "GeometryPoint"));
    }

    [Fact]
    public void CapacityCountIsDiagnosticAndNeverWrittenOutput()
    {
        object result = Refuse("CapacityExceeded", 257);
        Assert.Equal(257, Read(result, "RequiredCapacity"));
        Assert.Equal(0, Read(result, "Written"));
        Assert.Null(Read(result, "Lease"));
    }

    [Fact]
    public void RefusalFactoryCannotForgeCompleteOrMalformedCounts()
    {
        Assert.ThrowsAny<ArgumentException>(() => Refuse("Complete", 0));
        Assert.ThrowsAny<ArgumentException>(() => Refuse("255", 0));
        Assert.ThrowsAny<ArgumentException>(() => Refuse("CapacityExceeded", -1));
        Assert.ThrowsAny<ArgumentException>(() => Refuse("Unsupported", 1));
    }

    [Fact]
    public void OptionalQueryUsesTheActualLeaseAndCallerFaceSpan()
    {
        Type result = ResultType;
        Type capability = Required("IPhysicsCapsuleFeatures");
        Type lease = Required("IPhysicsQueryLease");
        Type face = Required("CapsuleIncidentFace");
        Assert.True(capability.IsInterface);
        MethodInfo query = Assert.Single(capability.GetMethods(), m => m.Name == "QueryCapsuleFeature");
        Assert.Equal(result, query.ReturnType);
        Assert.Equal(new[] { lease, typeof(StaticHandle), typeof(CapsuleShape), typeof(Pose), typeof(float),
            typeof(Span<>).MakeGenericType(face), typeof(QueryFilter) },
            query.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.True(query.GetParameters()[^1].HasDefaultValue);
        // The support neighborhood adds its own AssertFeatureCurrent overload. This one consumes feature results.
        MethodInfo current = Assert.Single(capability.GetMethods(), m => m.Name == "AssertFeatureCurrent" &&
            m.GetParameters()[0].ParameterType == result.MakeByRefType());
        Assert.Equal(typeof(void), current.ReturnType);
        Assert.Equal(new[] { result.MakeByRefType(), lease }, current.GetParameters().Select(p => p.ParameterType));
    }

    static Type Required(string name)
    {
        Type? type = typeof(IPhysicsWorld).Assembly.GetType("KhaozEngine.Physics." + name);
        Assert.NotNull(type);
        return type;
    }

    static object? Read(object value, string name)
    {
        PropertyInfo? property = ResultType.GetProperty(name);
        Assert.NotNull(property);
        Assert.Null(property.SetMethod);
        return property.GetValue(value);
    }

    static object Refuse(string status, int requiredCapacity)
    {
        Type result = ResultType;
        Type statusType = Required("CapsuleFeatureStatus");
        MethodInfo? factory = result.GetMethod("Refused", BindingFlags.Public | BindingFlags.Static,
            null, [statusType, typeof(int)], null);
        Assert.NotNull(factory);
        try { return factory.Invoke(null, [Enum.Parse(statusType, status), requiredCapacity])!; }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}

using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuPolicyRefusalConstructorTests
{
    [Theory]
    [InlineData(typeof(GpuBackendProviderMissingException), typeof(GpuBackendProviderMissingRefusal))]
    [InlineData(typeof(GpuBackendRetiredException), typeof(GpuBackendRetiredRefusal))]
    [InlineData(typeof(GpuNoUsableBackendException), typeof(GpuNoUsableBackendRefusal))]
    [InlineData(typeof(GpuNestedRecordingException), typeof(GpuNestedRecordingRefusal))]
    public void EngineCallersAlwaysSupplyAReason(Type exceptionType, Type reasonType)
    {
        var legacyConstructors = exceptionType.GetConstructors()
            .Where(constructor => constructor.GetParameters()
                .All(parameter => parameter.ParameterType != reasonType))
            .ToArray();

        Assert.NotEmpty(legacyConstructors);
        Assert.Empty(RefusalConstructorFence.EngineCallers(
            new[] { typeof(GpuDeviceContext).Assembly, typeof(KhaozEngine.Render3D.Scene3D).Assembly }, legacyConstructors));
    }
}

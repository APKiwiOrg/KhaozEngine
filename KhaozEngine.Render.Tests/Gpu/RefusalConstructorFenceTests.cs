using System;
using System.IO;
using KhaozEngine.Gpu.D3D11;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("GraphicsBackendGlobalState")]
public sealed class RefusalConstructorFenceTests
{
    [Fact]
    public void FindsACallerInThisAssembly()
    {
        var constructor = typeof(InvalidDataException).GetConstructor(new[] { typeof(string) })!;

        var callers = RefusalConstructorFence.EngineCallers(
            new[] { typeof(RefusalConstructorFenceTests).Assembly }, new[] { constructor });

        Assert.Contains($"{typeof(RefusalConstructorFenceTests).FullName}.{nameof(MessageOnlyControl)}", callers);
    }

    [Fact]
    public void ReportsNothingForAnUncalledConstructor()
    {
        var constructor = typeof(UncalledException).GetConstructor(new[] { typeof(string) })!;

        var callers = RefusalConstructorFence.EngineCallers(
            new[] { typeof(RefusalConstructorFenceTests).Assembly }, new[] { constructor });

        Assert.Empty(callers);
    }

    [Fact]
    public void ScanningAnInteropAssemblyLoadsNoReference()
    {
        if (KhaozEngineD3D11.IsPlatformSupported) return;

        // M7 wave 0 regression: resolving IL tokens must not load Windows-only interop.
        var constructor = typeof(InvalidDataException).GetConstructor(new[] { typeof(string) })!;
        RefusalConstructorFence.EngineCallers(
            new[] { typeof(KhaozEngineD3D11).Assembly }, new[] { constructor });

        D3D11InteropLoad.AssertNotLoaded();
    }

    static InvalidDataException MessageOnlyControl() => new("Constructor fence positive control.");

    sealed class UncalledException : Exception
    {
        public UncalledException(string message) : base(message) { }
    }
}

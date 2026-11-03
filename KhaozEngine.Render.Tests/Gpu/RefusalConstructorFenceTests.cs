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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindsConstructorChainingInThisAssembly(bool callsBase)
    {
        // M7 wave 0 review finding 1: base and this constructor calls use call rather than newobj.
        var constructor = typeof(ChainedException).GetConstructor(new[] { typeof(string) })!;

        var callers = RefusalConstructorFence.EngineCallers(
            new[] { typeof(RefusalConstructorFenceTests).Assembly }, new[] { constructor });

        var callerType = callsBase ? typeof(DerivedException) : typeof(ChainedException);
        Assert.Contains($"{callerType.FullName}..ctor", callers);
    }

    [Theory]
    [InlineData(nameof(GenericValueControl))]
    [InlineData(nameof(GenericMixedOperandsControl))]
    public void FindsConstructedGenericCallersWithoutMatchingOtherOverloads(string caller)
    {
        // M7 wave 0 review findings 2 and 3: substitute generic arguments and walk past mixed IL operands.
        var constructor = typeof(GenericException<int>).GetConstructor(new[] { typeof(int) })!;

        var callers = RefusalConstructorFence.EngineCallers(
            new[] { typeof(RefusalConstructorFenceTests).Assembly }, new[] { constructor });

        Assert.Contains($"{typeof(RefusalConstructorFenceTests).FullName}.{caller}", callers);
        Assert.DoesNotContain(
            $"{typeof(RefusalConstructorFenceTests).FullName}.{nameof(GenericOverloadControl)}", callers);
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

    static GenericException<int> GenericValueControl() => new(42);

    static GenericException<int> GenericOverloadControl() => new("Allowed overload.");

    static GenericException<int> GenericMixedOperandsControl(int value)
    {
        int argument = value switch
        {
            0 => 10,
            1 => 20,
            2 => 30,
            3 => 40,
            4 => 50,
            _ => 60,
        };
        GC.KeepAlive(value > 256);
        GC.KeepAlive(0x123456789ABCDEF);
        GC.KeepAlive(123.5);
        GC.KeepAlive(12.5f);
        return new GenericException<int>(argument);
    }

    class ChainedException : Exception
    {
        public ChainedException(string message) : base(message) { }
        public ChainedException() : this("Constructor chaining control.") { }
    }

    sealed class DerivedException : ChainedException
    {
        public DerivedException() : base("Base constructor control.") { }
    }

    sealed class GenericException<T> : Exception
    {
        public GenericException(T value) { }
        public GenericException(string message) : base(message) { }
    }

    sealed class UncalledException : Exception
    {
        public UncalledException(string message) : base(message) { }
    }
}

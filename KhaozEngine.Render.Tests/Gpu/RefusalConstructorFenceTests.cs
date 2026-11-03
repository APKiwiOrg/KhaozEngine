using System;
using System.IO;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class RefusalConstructorFenceTests
{
    [Fact]
    public void FindsACallerInThisAssembly()
    {
        var constructor = typeof(InvalidDataException).GetConstructor(new[] { typeof(string) })!;

        var callers = RefusalConstructorFence.EngineCallers(
            new[] { typeof(RefusalConstructorFenceTests).Assembly }, new[] { constructor });

        Assert.Contains($"{nameof(RefusalConstructorFenceTests)}.{nameof(MessageOnlyControl)}", callers);
    }

    [Fact]
    public void ReportsNothingForAnUncalledConstructor()
    {
        var constructor = typeof(UncalledException).GetConstructor(new[] { typeof(string) })!;

        var callers = RefusalConstructorFence.EngineCallers(
            new[] { typeof(RefusalConstructorFenceTests).Assembly }, new[] { constructor });

        Assert.Empty(callers);
    }

    static InvalidDataException MessageOnlyControl() => new("Constructor fence positive control.");

    sealed class UncalledException : Exception
    {
        public UncalledException(string message) : base(message) { }
    }
}

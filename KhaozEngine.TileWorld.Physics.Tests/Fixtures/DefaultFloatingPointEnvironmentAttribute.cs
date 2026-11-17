using System;
using System.Reflection;
using Xunit.Sdk;

[assembly: KhaozEngine.Tests.TileWorld.Physics.Fixtures.DefaultFloatingPointEnvironment]

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>Fails any test in this assembly that starts or ends with a changed rounding or flush control on its
/// thread (#1830). A changed control outlives its test in two ways. Code the JIT compiles on that thread keeps
/// flushed float subnormal constants for every thread, and on Linux a thread created under it inherits it. Either
/// way a later test reads results it did not ask for, so a test that changes the controls restores them on its own
/// thread, writes no test output and starts no thread while they are changed.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class DefaultFloatingPointEnvironmentAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) =>
        Require(WaterFloatingPointEnvironment.CurrentModes(), Name(methodUnderTest), "started");

    public override void After(MethodInfo methodUnderTest) =>
        Require(WaterFloatingPointEnvironment.CurrentModes(), Name(methodUnderTest), "ended");

    internal static void Require(ulong? modes, string test, string phase)
    {
        if (modes is { } changed && changed != 0)
            throw new InvalidOperationException(
                $"{test} {phase} with floating point controls 0x{changed:X} on managed thread " +
                $"{Environment.CurrentManagedThreadId}. Every test starts and ends under the default environment.");
    }

    static string Name(MethodInfo method) => $"{method.DeclaringType?.Name}.{method.Name}";
}

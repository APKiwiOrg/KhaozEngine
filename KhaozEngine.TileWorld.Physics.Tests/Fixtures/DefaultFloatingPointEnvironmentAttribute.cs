using System;
using System.Reflection;
using System.Threading;
using Xunit.Sdk;

[assembly: KhaozEngine.Tests.TileWorld.Physics.Fixtures.DefaultFloatingPointEnvironment]

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>Fails any test in this assembly that starts or ends with a changed rounding or flush control on its
/// thread, naming the test, the controls and the thread. KhaozEngine.TileWorld.Physics.Tests cannot set a control,
/// so there it must never trip, and a failure names a thread that reached a test with a control nothing in that
/// process set. In the floating point test assemblies, KhaozEngine.TileWorld.Physics.FloatingPoint.Tests and
/// KhaozEngine.FloatingPoint.Tests, whose tests change controls on purpose, it fails the test that left one changed
/// instead of a later test that reads it.
/// <para>Limits. It reads only the thread that runs the test method, once before and once after it. Class
/// constructors, fixtures and test-class Dispose run outside that window, and work a test hands to other threads is
/// unchecked. It cannot see machine code the JIT compiled while a control was changed, which serves every thread for
/// the rest of the process. That is why tests that change controls run in their own test process.</para></summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class DefaultFloatingPointEnvironmentAttribute : BeforeAfterTestAttribute
{
    static int _before;

    /// <summary>How many tests the guard has checked before they ran in this process.</summary>
    internal static int BeforeCountForTests => Volatile.Read(ref _before);

    public override void Before(MethodInfo methodUnderTest)
    {
        Interlocked.Increment(ref _before);
        Require(WaterFloatingPointEnvironment.CurrentModes(), Name(methodUnderTest), "started");
    }

    public override void After(MethodInfo methodUnderTest) =>
        Require(WaterFloatingPointEnvironment.CurrentModes(), Name(methodUnderTest), "ended");

    internal static void Require(ulong? modes, string test, string phase)
    {
        if (modes is { } changed && changed != 0)
        {
            var thread = Thread.CurrentThread;
            throw new InvalidOperationException(
                $"{test} {phase} with floating point controls 0x{changed:X} on managed thread " +
                $"{Environment.CurrentManagedThreadId} (pool thread {thread.IsThreadPoolThread}, name " +
                $"'{thread.Name ?? "unnamed"}'). Every test starts and ends under the default environment.");
        }
    }

    static string Name(MethodInfo method) => $"{method.DeclaringType?.Name}.{method.Name}";
}

using System.Runtime.CompilerServices;

namespace KhaozEngine.Tests;

/// <summary>Raises this test host's <see cref="ThreadPoolFloor"/> before the first test runs.</summary>
internal static class ThreadPoolFloorHook
{
    [ModuleInitializer]
    internal static void Raise() => ThreadPoolFloor.Raise();
}

using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests;

/// <summary>Holds allocation windows until background D3D11 shader compiles finish, in a test assembly that
/// references the D3D11 backend. Linked by each such project. See README.md.</summary>
internal static class D3D11CompileQuiesce
{
    // A hang guard for a stuck compile, not a timing budget. Normal waits end when the compiles do.
    static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    [ModuleInitializer]
    internal static void Register() => AllocationRegion.BeforeWindow = () => D3D11ShaderCompileWorkers.WaitForIdleForTests(Limit);
}

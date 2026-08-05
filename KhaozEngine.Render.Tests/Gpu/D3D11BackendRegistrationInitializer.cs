using System.Runtime.CompilerServices;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE BELT, for the registry tests in this assembly that never touch a <c>[GpuFact]</c>.
    /// <para>
    /// <see cref="GpuFactAttribute"/>'s static constructor is the braces, and it covers every assembly that runs
    /// GPU tests, because it fires the first time xUnit's discovery pass reads that attribute. This assembly also
    /// holds plain <c>[Fact]</c>s that assert the process really does have the REAL native provider registered
    /// (<c>D3D11BackendRegistrationTests</c>), and those can be selected and run on their own with the attribute
    /// never loaded at all. They had blanket coverage while the module initializer lived in this project, so it
    /// stays here in its thin form rather than leaving a filter away from being a mystery failure.
    /// </para>
    /// <para>
    /// CA2255 is fine HERE and is an error in the support library, which is not an inconsistency. A test project
    /// is application code with a generated entry point, and the guarantee a library cannot make (that the
    /// assembly is loaded at all) is the one a test assembly makes by definition. The registration work itself
    /// stays in <see cref="D3D11BackendRegistration"/>, shared, idempotent and thread-safe, so this and the
    /// attribute hook cannot register two different things or register twice.
    /// </para>
    /// </summary>
    internal static class D3D11BackendRegistrationInitializer
    {
        [ModuleInitializer]
        internal static void RegisterNativeD3D11() => D3D11BackendRegistration.EnsureRegistered();
    }
}

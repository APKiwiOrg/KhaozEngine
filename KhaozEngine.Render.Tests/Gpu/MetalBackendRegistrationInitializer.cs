using System.Runtime.CompilerServices;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE BELT for the native METAL registration, the exact sibling of
    /// <see cref="D3D11BackendRegistrationInitializer"/> and
    /// <see cref="VulkanBackendRegistrationInitializer"/>, and here for the same one reason.
    /// <para>
    /// <see cref="GpuFactAttribute"/>'s static constructor registers all three native backends and covers every
    /// assembly that runs GPU tests, because it fires during xUnit's discovery pass. This assembly also holds
    /// plain <c>[Fact]</c>s asserting the process really does have the REAL Metal provider registered
    /// (<c>MetalBackendRegistrationTests</c>), and a filtered run of exactly those never touches the attribute
    /// and never fires that hook. So the belt is here, thin, delegating to the shared idempotent registration.
    /// </para>
    /// <para>
    /// A separate type rather than a third line in one of the others, deliberately: the three are independent
    /// facts about three independent packages, and the day one of them goes away the others must not have to be
    /// untangled from it. All are idempotent and thread-safe, so having three of them cannot register twice or
    /// register different things. CA2255 is fine here for the reason stated on the siblings: a test project is
    /// application code, and the load guarantee a library cannot make is one a test assembly makes by
    /// definition.
    /// </para>
    /// </summary>
    internal static class MetalBackendRegistrationInitializer
    {
        [ModuleInitializer]
        internal static void RegisterNativeMetal() => MetalBackendRegistration.EnsureRegistered();
    }
}

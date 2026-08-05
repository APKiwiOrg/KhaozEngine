using System.Runtime.CompilerServices;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE BELT for the native VULKAN registration, the exact sibling of
    /// <see cref="D3D11BackendRegistrationInitializer"/> and here for the same one reason.
    /// <para>
    /// <see cref="GpuFactAttribute"/>'s static constructor registers both native backends and covers every
    /// assembly that runs GPU tests, because it fires during xUnit's discovery pass. This assembly also holds
    /// plain <c>[Fact]</c>s asserting the process really does have the REAL Vulkan provider registered
    /// (<c>VulkanBackendRegistrationTests</c>), and a filtered run of exactly those never touches the attribute
    /// and never fires that hook. So the belt is here, thin, delegating to the shared idempotent registration.
    /// </para>
    /// <para>
    /// A separate type rather than a second line in the Direct3D 11 initializer, deliberately: the two are
    /// independent facts about two independent packages, and the day one of them goes away the other must not
    /// have to be untangled from it. Both are idempotent and thread-safe, so having two of them cannot register
    /// twice or register different things. CA2255 is fine here for the reason stated on the sibling: a test
    /// project is application code, and the load guarantee a library cannot make is one a test assembly makes by
    /// definition.
    /// </para>
    /// </summary>
    internal static class VulkanBackendRegistrationInitializer
    {
        [ModuleInitializer]
        internal static void RegisterNativeVulkan() => VulkanBackendRegistration.EnsureRegistered();
    }
}

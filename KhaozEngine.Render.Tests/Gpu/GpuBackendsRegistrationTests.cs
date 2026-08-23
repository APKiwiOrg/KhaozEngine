using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The boot registration <c>AppWindow</c> and both snapshot hosts make, and the one property it exists for:
    /// the backend a process is about to ASK for has a provider by the time it asks. Registering this platform's
    /// own kind is not the same statement, because a stored preference and <c>KE_GRAPHICS_BACKEND</c> both
    /// outrank the OS probe.
    /// <para>
    /// Device-free. Nothing here creates a device or loads a driver: registration seats a provider object, and
    /// the functional probe behind it is never asked.
    /// </para>
    /// <para>
    /// It writes the process-wide provider registry under REAL kinds, so it belongs off the parallel pool with
    /// the rest of the graphics global state, and every row puts back exactly what it found. The assembly's own
    /// registrations outlive this file and every <c>GpuFact</c> after it needs them.
    /// </para>
    /// </summary>
    [Collection("GraphicsBackendGlobalState")]
    public sealed class GpuBackendsRegistrationTests
    {
        static readonly GpuBackendKind[] _natives =
        {
            GpuBackendKind.MetalNative, GpuBackendKind.Direct3D11Native, GpuBackendKind.VulkanNative,
        };

        /// <summary>
        /// THE FIX, on the case that produced it: a stored preference for a native this platform does not
        /// default to leaves that native REGISTERED. Before this, boot registered the platform's own kind and
        /// the very next call asked for the preference and was told to add a package the umbrella already ships.
        /// <para>
        /// Driven with the preference the caller actually holds rather than through the environment, because
        /// that is the shape <c>AppWindow</c> passes and the shape the bug lived in: the registration used to
        /// resolve without the preference at all.
        /// </para>
        /// </summary>
        [Fact]
        public void ItRegistersTheStoredPreference_NotOnlyThePlatformDefault()
        {
            GpuBackendKind platform = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());
            GpuBackendKind foreign = ANativeOtherThan(platform);

            using var _ = new RegistryScope(_natives);
            using var env = new EnvScope(GpuBackendSelector.EnvVarName, null);
            Unregister(_natives);

            GpuBackendKind? answer = GpuBackends.RegisterResolvedIfUnregistered(foreign);

            Assert.Equal(GpuBackends.CanRegisterHere(foreign) ? foreign : platform, answer);
            Assert.Equal(GpuBackends.CanRegisterHere(foreign), GpuBackendProviders.IsRegistered(foreign));
            // The fallback target, always, because a resolved kind that fails on this machine has to land
            // somewhere. Registering the request alone would turn one refusal into two.
            Assert.True(GpuBackendProviders.IsRegistered(platform));
        }

        /// <summary>
        /// DEFECT 2, the env-pinned foreign native: <c>KE_GRAPHICS_BACKEND=vulkan-native</c> on a Mac used to
        /// register Metal and then tell the developer to add the Vulkan package, which the
        /// <c>KhaozEngine.Game2D</c> and <c>KhaozEngine.Game3D</c> umbrellas already ship. The request now
        /// reaches the PROVIDER, so what refuses it is that backend's own answer about this machine rather than
        /// a message about a missing package.
        /// <para>
        /// Pinned to Vulkan on purpose: it is the one native with no platform guard, so this row asserts the
        /// same thing on every OS the suite runs on.
        /// </para>
        /// </summary>
        [Fact]
        public void AnEnvironmentPinnedForeignNative_ReachesItsProvider_RatherThanAMissingPackageMessage()
        {
            using var _ = new RegistryScope(_natives);
            using var env = new EnvScope(GpuBackendSelector.EnvVarName, "vulkan-native");
            Unregister(_natives);

            GpuBackends.RegisterResolvedIfUnregistered();

            Assert.True(GpuBackendProviders.IsRegistered(GpuBackendKind.VulkanNative));

            // No fallback allowance, which is what a pinned headless capture takes, so the preflight either
            // clears the request or throws. Clearing it is the fix: the missing-package throw is gone and what
            // is left to refuse the run is the provider itself.
            GpuBackendSelection pinned = GpuBackendSelector.Resolve(userPreference: null);
            Assert.Equal(GpuBackendKind.VulkanNative, pinned.Backend);
            Assert.Null(GpuDeviceContext.PreflightProvider(
                pinned, allowFallback: false, out IGpuBackendProvider? provider));
            Assert.NotNull(provider);
        }

        /// <summary>
        /// A HOST REGISTRATION STAYS AUTHORITATIVE, per kind. Registration is last-writer-wins and the boot call
        /// writes late, so a wrapper or a fake seated by a host would otherwise be replaced by the stock
        /// provider at window creation, silently.
        /// </summary>
        [Fact]
        public void ItLeavesAnAlreadyRegisteredProviderAlone()
        {
            GpuBackendKind platform = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());
            var mine = new FakeBackendProvider(platform);

            using var _ = new RegistryScope(_natives);
            using var env = new EnvScope(GpuBackendSelector.EnvVarName, null);
            Unregister(_natives);
            GpuBackendProviders.Register(platform, mine);

            Assert.Null(GpuBackends.RegisterResolvedIfUnregistered());

            GpuBackendProviders.TryGet(platform, out IGpuBackendProvider? found);
            Assert.Same(mine, found);
        }

        /// <summary>
        /// The platform guard, stated as the mapping it has to keep: Vulkan is registerable everywhere because
        /// its package is not OS-specific, the other two only on their own platform, and a RETIRED kind nowhere.
        /// A guard that answered true off-platform would seat a provider that refuses everything and turn an
        /// honest "there is no Direct3D 11 on this machine" into a machine-capability answer.
        /// </summary>
        [Fact]
        public void CanRegisterHere_IsTrueForVulkanEverywhere_AndOtherwiseOnlyForThisPlatformsOwn()
        {
            GpuBackendKind platform = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());

            foreach (GpuBackendKind kind in _natives)
            {
                Assert.Equal(kind == GpuBackendKind.VulkanNative || kind == platform,
                    GpuBackends.CanRegisterHere(kind));
            }

            foreach (GpuBackendKind retired in new[]
            {
                GpuBackendKind.Metal, GpuBackendKind.Vulkan, GpuBackendKind.Direct3D11, GpuBackendKind.OpenGL,
            })
            {
                Assert.False(GpuBackends.CanRegisterHere(retired));
            }
        }

        static GpuBackendKind ANativeOtherThan(GpuBackendKind platform)
            => platform == GpuBackendKind.VulkanNative ? GpuBackendKind.MetalNative : GpuBackendKind.VulkanNative;

        static void Unregister(IEnumerable<GpuBackendKind> kinds)
        {
            foreach (GpuBackendKind kind in kinds) GpuBackendProviders.Unregister(kind);
        }

        // Remembers what the registry held for a set of kinds and puts it back, because these rows deliberately
        // clear real entries the rest of the assembly depends on.
        sealed class RegistryScope : IDisposable
        {
            readonly List<(GpuBackendKind Kind, IGpuBackendProvider? Provider)> _prior = new();

            internal RegistryScope(IEnumerable<GpuBackendKind> kinds)
            {
                foreach (GpuBackendKind kind in kinds)
                {
                    GpuBackendProviders.TryGet(kind, out IGpuBackendProvider? provider);
                    _prior.Add((kind, provider));
                }
            }

            public void Dispose()
            {
                foreach ((GpuBackendKind kind, IGpuBackendProvider? provider) in _prior)
                {
                    if (provider is null) GpuBackendProviders.Unregister(kind);
                    else GpuBackendProviders.Register(kind, provider);
                }
            }
        }
    }
}

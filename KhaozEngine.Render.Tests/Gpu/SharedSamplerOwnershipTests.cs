using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A CONSUMER THAT DISPOSES <c>IGpuDevice.PointSampler</c> DESTROYS NOTHING
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/506).
    ///
    /// <para><b>THE PROTECTION WAS DOCUMENTED AND ABSENT ON TWO BACKENDS.</b> Direct3D 11's sampler wrapper
    /// carried an <c>ownsSampler</c> flag whose doc described exactly this rule, and the only creation path in
    /// the backend passed it true, so the device's shared pair was owned by whoever disposed it first. Metal had
    /// no flag at all. Vulkan was the only one that guarded it. Nothing in the engine disposes the pair today,
    /// which is why this was latent rather than a live crash: the device would have released two sampler states
    /// it no longer had, and every bind of the pair after that would have named a freed object.</para>
    ///
    /// <para><b>THE TWO HALVES ARE TESTED DIFFERENTLY BECAUSE ONE OF THEM HAS NO DEVICE HERE.</b> The Direct3D
    /// 11 sampler cannot be constructed without an <c>ID3D11Device</c>, so its half is the WIRING, read off the
    /// compiled assembly: the device builds its pair through the factory's non-owning path, and its teardown
    /// releases them through the destroy that a consumer's <c>Dispose</c> deliberately is not. The Metal half
    /// runs on this machine's real device and observes the rule directly.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class SharedSamplerOwnershipTests
    {
        readonly ITestOutputHelper _output;

        public SharedSamplerOwnershipTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// THE DIRECT3D 11 WIRING. Two facts, and neither implies the other: the pair is created through the
        /// factory's non-owning path, so a consumer's <c>Dispose</c> is a no-op, AND the teardown releases it
        /// through <c>DestroyShared</c>, which is the only call that frees a non-owning sampler. Doing the first
        /// without the second would turn a latent double-free into a certain leak of both sampler states on
        /// every device teardown.
        /// </summary>
        [Fact]
        public void TheDirect3D11SharedPair_IsCreatedNonOwningAndReleasedByTheDeviceItself()
        {
            using var backend = D3D11BackendMetadata.Open();

            Assert.Equal(2, backend.CallCountIn("D3D11GpuDevice", ".ctor",
                "D3D11ResourceFactory.CreateSharedSampler"));
            Assert.Equal(0, backend.CallCountIn("D3D11GpuDevice", ".ctor", "D3D11ResourceFactory.CreateSampler"));
            Assert.Equal(2, backend.CallCountIn("D3D11GpuDevice", "MarkDeviceDisposed",
                "D3D11Sampler.DestroyShared"));
        }

        /// <summary>
        /// A FAILED CONSTRUCTION UNWINDS THE PAIR THROUGH THE SAME DESTROY, which is the interaction between this
        /// and https://github.com/APKiwiOrg/KhaozEngine/issues/503 that is easy to get wrong: the construction
        /// scope registers a release rather than a <c>Dispose</c> for these two, because their <c>Dispose</c> is
        /// the no-op that protects them. Registering the no-op would leak both sampler states on the swapchain
        /// failure the unwind exists for.
        /// </summary>
        [Fact]
        public void AFailedDirect3D11Construction_UnwindsTheSharedPairThroughItsDestroy()
        {
            using var backend = D3D11BackendMetadata.Open();

            Assert.Equal(2, backend.CallCountIn("D3D11GpuDevice", ".ctor",
                "D3D11ConstructionScope.TrackRelease"));
        }

        /// <summary>
        /// THE METAL HALF, ON A REAL DEVICE. The pair reports itself non-owning, and disposing what the seam
        /// handed back leaves the bind path's handle exactly where it was. That handle is the assertion that
        /// matters: <c>MetalSampler.Handle</c> is read AT THE BIND rather than copied into a resource set, so a
        /// wrapper that marked itself disposed would hand the next encoder a nil sampler.
        /// <para>
        /// It reads the handle rather than messaging the object after the dispose, deliberately. If the fix were
        /// absent the sampler state would be released by then, and messaging a freed object is undefined
        /// behaviour that could take the runner down instead of failing. A nil handle fails cleanly and is the
        /// same defect.
        /// </para>
        /// </summary>
        [GpuFact]
        public void AConsumerDisposingTheSharedPair_LeavesTheDevicesOwnSamplersAlone()
        {
            if (!Available()) return;

            using MetalGpuDevice device = CreateHeadless();

            var point = (MetalSampler)device.PointSampler;
            var linear = (MetalSampler)device.LinearSampler;

            Assert.False(point.OwnsSampler, "the device's point sampler was created owning");
            Assert.False(linear.OwnsSampler, "the device's linear sampler was created owning");

            IntPtr pointHandle = ((IMetalBindable)point).BindHandle;
            IntPtr linearHandle = ((IMetalBindable)linear).BindHandle;
            Assert.NotEqual(IntPtr.Zero, pointHandle);
            Assert.NotEqual(IntPtr.Zero, linearHandle);
            _output.WriteLine($"the shared pair is {ObjCClassOf(pointHandle)} and {ObjCClassOf(linearHandle)}");

            // What a consumer does when it treats what the seam handed back as its own.
            device.PointSampler.Dispose();
            device.LinearSampler.Dispose();

            Assert.Equal(pointHandle, ((IMetalBindable)point).BindHandle);
            Assert.Equal(linearHandle, ((IMetalBindable)linear).BindHandle);

            // And the same two objects are still what the device hands the NEXT caller, since the device holds
            // one pair for its life rather than making a sampler per request.
            Assert.Same(point, device.PointSampler);
            Assert.Same(linear, device.LinearSampler);
        }

        /// <summary>
        /// THE OTHER HALF ON METAL: the device still destroys the pair itself. A non-owning wrapper that nothing
        /// destroyed would leak both sampler states on every device teardown, which is a worse bug than the
        /// double-free it replaced because it happens on the ordinary path rather than a path nothing takes.
        /// <para>
        /// Read through the wrapper's own handle going nil, which is what its release sets, and taken AFTER a
        /// consumer already disposed both, so this also pins that the consumer's no-op did not disarm the
        /// device's release.
        /// </para>
        /// </summary>
        [GpuFact]
        public void TheMetalDeviceStillDestroysTheSharedPairAtTeardown()
        {
            if (!Available()) return;

            MetalGpuDevice device = CreateHeadless();
            var point = (MetalSampler)device.PointSampler;
            var linear = (MetalSampler)device.LinearSampler;

            device.PointSampler.Dispose();
            device.LinearSampler.Dispose();
            Assert.NotEqual(IntPtr.Zero, ((IMetalBindable)point).BindHandle);

            device.Dispose();

            Assert.Equal(IntPtr.Zero, ((IMetalBindable)point).BindHandle);
            Assert.Equal(IntPtr.Zero, ((IMetalBindable)linear).BindHandle);
        }

        [SupportedOSPlatform("macos")]
        static string ObjCClassOf(IntPtr handle)
            => KhaozEngine.Gpu.Metal.Internal.ObjC.ObjCRuntime.ClassNameOf(handle);

        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (KhaozEngineMetal.IsPlatformSupported) return true;

            MetalDormancy.ThrowIfRequired("this is not macOS at all");
            _output.WriteLine("dormant: not macOS, so there is no Metal device whose shared samplers to read.");
            return false;
        }

        static MetalGpuDevice CreateHeadless()
            => (MetalGpuDevice)new MetalBackendProvider().CreateHeadless().Device;
    }
}

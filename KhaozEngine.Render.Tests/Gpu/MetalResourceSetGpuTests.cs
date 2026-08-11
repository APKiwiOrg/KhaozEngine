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
    /// ROW 10'S RESOURCE SET AGAINST REAL RESOURCES (https://github.com/APKiwiOrg/KhaozEngine/issues/576). The
    /// layout's whole decision surface is device-free and lives in <c>MetalResourceLayoutTests</c>. What is left
    /// here is a set's RESOLUTION, which is a check against wrappers only a device can make: a real
    /// <c>MTLBuffer</c>, a real <c>MTLTexture</c>, a real <c>MTLSamplerState</c>, and the ring a uniform buffer
    /// was cut into at creation.
    ///
    /// <para><b>NOTHING HERE IS A NATIVE CALL, WHICH IS THE POINT OF THE ROW.</b> A set on this backend is
    /// resolved managed data: Metal's answer to a descriptor set is an argument buffer and section 8.4 declines
    /// them by name. So these rows need a device only to have resources that were really created on one, and the
    /// thing they assert is that the resolution reads the right handle, the right window and the right ring off
    /// them.</para>
    ///
    /// <para><b>EVERYTHING IS RESOLVED AT CREATION AND NOTHING AT A BIND</b>, which is why the assertions are
    /// about the state of a freshly built set rather than about a call. A set is created once at load time across
    /// 68 shipped call sites and bound thousands of times a frame, so a type check or a ring lookup done at a bind
    /// is done for nothing, and a resolution that had been deferred would show up here as a set that knows
    /// nothing yet.</para>
    ///
    /// <para><b>IT SITS IN <c>NativeDeviceLifecycle</c></b> because every row builds a whole <c>MTLDevice</c> and
    /// queue and tears it down, which is the collection's condition rather than a preference.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalResourceSetGpuTests
    {
        readonly ITestOutputHelper _output;

        public MetalResourceSetGpuTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// ONE SET OF EVERY SPACE, RESOLVED. The three argument tables are independent on Metal, so a set has to
        /// carry which one each binding goes in as well as the handle, and index 0 means three different things
        /// without it.
        /// </summary>
        [GpuFact]
        public void ASetOfEverySpace_ResolvesToTheHandleTheSpaceAndTheWindow()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                Element("Frame", GpuResourceKind.UniformBuffer, dynamic: true),
                Element("Bones", GpuResourceKind.StructuredBufferReadOnly),
                Element("Albedo", GpuResourceKind.TextureReadOnly),
                Element("AlbedoSampler", GpuResourceKind.Sampler)));

            using IGpuBuffer frame = factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer bones = factory.CreateBuffer(
                new GpuBufferDescription(512, GpuBufferUsage.StructuredBufferReadOnly));
            using IGpuTexture albedo = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            using IGpuSampler sampler = factory.CreateSampler(GpuSamplerDescription.Linear);

            using IGpuResourceSet set = factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, frame, bones, albedo, sampler));

            var metal = (MetalResourceSet)set;
            Assert.Equal(4, metal.Bindings.Length);
            Assert.Same(layout, metal.Layout);

            MetalBoundResource uniform = metal.Bindings[0];
            Assert.Equal(MetalIndexSpace.Buffer, uniform.Space);
            Assert.NotEqual(IntPtr.Zero, uniform.Handle);
            Assert.Equal(0u, uniform.RangeOffset);

            // THE LOGICAL SIZE AND NOT THE ALLOCATION. A ring-backed uniform buffer is FramesInFlight segments
            // wide, and a window covering the allocation would span every frame's copy at once.
            Assert.Equal(256u, uniform.Range);

            // THE RING IS CARRIED so row 13 can compose frameBase + rangeOffset + callerDynamicOffset, and the
            // declared Dynamic flag is the only thing that decides whether the caller's term is added.
            Assert.NotNull(uniform.Ring);
            Assert.True(uniform.AppliesCallerOffset);

            MetalBoundResource structured = metal.Bindings[1];
            Assert.Equal(MetalIndexSpace.Buffer, structured.Space);
            Assert.Equal(512u, structured.Range);
            Assert.Null(structured.Ring);
            Assert.False(structured.AppliesCallerOffset);

            Assert.Equal(MetalIndexSpace.Texture, metal.Bindings[2].Space);
            Assert.NotEqual(IntPtr.Zero, metal.Bindings[2].Handle);
            Assert.Equal(MetalIndexSpace.Sampler, metal.Bindings[3].Space);
            Assert.NotEqual(IntPtr.Zero, metal.Bindings[3].Handle);

            // The three spaces really are three different handles, so nothing collapsed on the way through.
            Assert.NotEqual(metal.Bindings[0].Handle, metal.Bindings[2].Handle);
            Assert.NotEqual(metal.Bindings[2].Handle, metal.Bindings[3].Handle);
        }

        /// <summary>
        /// A <see cref="GpuBufferRange"/> PINS ITS OWN WINDOW, resolved once here rather than read at every bind.
        /// The offset is fixed for the set's life and the caller's per-draw offset is added on top of it at bind
        /// time, which is what lets many draws read their own slice of one buffer.
        /// </summary>
        [GpuFact]
        public void ABufferRange_ResolvesToItsOwnOffsetAndSize()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                Element("Slice", GpuResourceKind.StructuredBufferReadOnly)));
            using IGpuBuffer buffer = factory.CreateBuffer(
                new GpuBufferDescription(1024, GpuBufferUsage.StructuredBufferReadOnly));

            using IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(
                layout, new GpuBufferRange(buffer, 256, 128)));

            MetalBoundResource bound = ((MetalResourceSet)set).Bindings[0];
            Assert.Equal(256u, bound.RangeOffset);
            Assert.Equal(128u, bound.Range);
            Assert.False(bound.AppliesCallerOffset);

            // A window that leaves the buffer is refused where the caller can still see it, because Metal's own
            // setters carry no length and nothing downstream would ever report it.
            Assert.Contains("binds 128 bytes at offset 960",
                Refusal(() => factory.CreateResourceSet(new GpuResourceSetDescription(
                    layout, new GpuBufferRange(buffer, 960, 128)))),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// EVERY REFUSAL A RESOLUTION OWNS, in one row, because they share a fixture and each is one line. A set
        /// is resolved once and never again, so each of these is a mistake with no later point at which it could
        /// come right.
        /// </summary>
        [GpuFact]
        public void EveryResolutionRefusal_NamesTheElementAndWhatArrived()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                Element("Frame", GpuResourceKind.UniformBuffer),
                Element("Albedo", GpuResourceKind.TextureReadOnly)));

            using IGpuBuffer frame = factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using IGpuTexture albedo = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            using IGpuTexture staging = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));

            // The positive control first, so every refusal below is about what it says rather than about the
            // fixture being wrong.
            factory.CreateResourceSet(new GpuResourceSetDescription(layout, frame, albedo)).Dispose();

            Assert.Contains("matched to elements POSITIONALLY",
                Refusal(() => factory.CreateResourceSet(new GpuResourceSetDescription(layout, frame))),
                StringComparison.Ordinal);

            Assert.Contains("'Albedo' at binding 1",
                Refusal(() => factory.CreateResourceSet(
                    new GpuResourceSetDescription(layout, frame, null!))),
                StringComparison.Ordinal);

            // A TEXTURE WHERE A BUFFER IS DECLARED, which is what a resource array one step out of line with its
            // layout actually looks like.
            Assert.Contains("declares UniformBuffer, which needs a buffer",
                Refusal(() => factory.CreateResourceSet(
                    new GpuResourceSetDescription(layout, albedo, albedo))),
                StringComparison.Ordinal);

            // A STAGING TEXTURE IS A Shared MTLBuffer AND NOT AN MTLTexture AT ALL (M-C5), so it has no handle for
            // the texture table and binding it would be nil at the draw with nothing pointing back here.
            string refused = Refusal(() => factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, frame, staging)));
            _output.WriteLine(refused);
            Assert.Contains("STAGING texture", refused, StringComparison.Ordinal);

            // A TEXTURE BOUND FOR A DIRECTION IT WAS NOT CREATED FOR, through the real factory, so the usage the
            // description declared really is what reaches the check. Sampled-only into a read-write element is
            // the shape that would otherwise land in the argument table with no ShaderWrite bit.
            using IGpuResourceLayout storage = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                Element("Target", GpuResourceKind.TextureReadWrite)));

            Assert.Contains("Add GpuTextureUsage.Storage",
                Refusal(() => factory.CreateResourceSet(new GpuResourceSetDescription(storage, albedo))),
                StringComparison.Ordinal);

            // AND A DISPOSED RESOURCE ANSWERS A NIL HANDLE. Creation is where a resource that is ALREADY gone is
            // still in front of the caller, and one disposed later degrades to the same nil at the bind on its
            // own, because a binding holds the wrapper rather than a copy of its handle.
            IGpuTexture disposed = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            disposed.Dispose();

            Assert.Contains("already been disposed",
                Refusal(() => factory.CreateResourceSet(
                    new GpuResourceSetDescription(layout, frame, disposed))),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// A LAYOUT OR A RESOURCE FROM ANOTHER DEVICE IS REFUSED BY NAME. Both are plain managed objects here, so
        /// nothing about the mistake is visible without the identity check: on Apple silicon the whole process
        /// shares one <c>MTLDevice</c>, so the bind would even work, and the failure would move to whichever
        /// device tore down first.
        /// </summary>
        [GpuFact]
        public void ALayoutOrAResourceFromAnotherDevice_IsRefused()
        {
            if (!Available()) return;

            using IGpuDevice first = CreateHeadless();
            using IGpuDevice second = CreateHeadless();

            using IGpuResourceLayout mine = first.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(Element("Frame", GpuResourceKind.UniformBuffer)));
            using IGpuBuffer myBuffer = first.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer theirBuffer = second.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));

            Assert.Contains("DIFFERENT native Metal device",
                Refusal(() => second.Factory.CreateResourceSet(
                    new GpuResourceSetDescription(mine, theirBuffer))),
                StringComparison.Ordinal);

            Assert.Contains("DIFFERENT native Metal device",
                Refusal(() => first.Factory.CreateResourceSet(
                    new GpuResourceSetDescription(mine, theirBuffer))),
                StringComparison.Ordinal);

            // And the same call on the device that created both still works, so the rule is about identity rather
            // than about having broken the entry point.
            first.Factory.CreateResourceSet(new GpuResourceSetDescription(mine, myBuffer)).Dispose();
        }

        /// <summary>
        /// TWO SHADER SETS COMPILED FROM ONE PROGRAM SHARE ONE INDEX TABLE, on a real device, through the cache
        /// the device owns. This is the dedup suite's device-free property arriving through the factory: what
        /// makes M-R9's pipeline-switch comparison a handle compare is that the tables a pipeline can reach are
        /// canonical, and the factory is the only route a pipeline has to one.
        /// </summary>
        [GpuFact]
        public void TwoShaderSetsOfOneProgram_ShareTheDevicesCanonicalTable()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            var metalDevice = (MetalGpuDevice)device;

            const string vertex = "#version 450\nvoid main() { gl_Position = vec4(0.0); }\n";
            const string fragment = "#version 450\nlayout(location = 0) out vec4 c;\nvoid main() { c = vec4(1.0); }\n";

            Assert.Equal(0, metalDevice.IndexTables.Count);

            using IGpuShaderSet first = device.Factory.CreateShadersFromSpirv(vertex, fragment);
            using IGpuShaderSet second = device.Factory.CreateShadersFromSpirv(vertex, fragment);

            MetalShaderIndexTable mine = ((MetalShaderSet)first).Table;
            MetalShaderIndexTable theirs = ((MetalShaderSet)second).Table;

            Assert.Same(mine, theirs);
            Assert.True(mine.SameIndicesAs(theirs));
            Assert.Equal(1, metalDevice.IndexTables.Count);
        }

        /// <summary>Disposal releases nothing and the resources outlive it, because a Metal resource set owns no
        /// Objective-C object at all. The flag exists so a use-after-dispose is a stated error.</summary>
        [GpuFact]
        public void DisposingASet_ReleasesNothingItNamed()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                Element("Frame", GpuResourceKind.UniformBuffer)));
            using IGpuBuffer frame = factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer));

            IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(layout, frame));
            var metal = (MetalResourceSet)set;

            Assert.False(metal.IsDisposed);
            set.Dispose();
            set.Dispose();
            Assert.True(metal.IsDisposed);

            // The buffer is still usable, and a second set over it still resolves, so nothing was released with
            // the first one.
            device.UpdateBuffer(frame, 0, new byte[16]);
            factory.CreateResourceSet(new GpuResourceSetDescription(layout, frame)).Dispose();
        }

        static GpuResourceLayoutElement Element(string name, GpuResourceKind kind, bool dynamic = false)
            => new(name, kind, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic);

        static IGpuDevice CreateHeadless() => new MetalBackendProvider().CreateHeadless().Device;

        static string Refusal(Func<object> call) => Assert.ThrowsAny<Exception>(() => call()).Message;

        // [SupportedOSPlatformGuard] rather than an inline check at every call site, the same mechanism the
        // sibling GPU suites use. Dormant off macOS rather than skipped, which is phase 3's row-19 lesson.
        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                // KE_METAL_REQUIRED=1 turns this into a throw on the leg that declared a device mandatory.
                MetalDormancy.ThrowIfRequired("this is not macOS at all");
                _output.WriteLine("dormant: not macOS, so there is no Metal device to build a resource set on.");
                return false;
            }

            string? missing = MetalSupportProbe.MissingRequirement();
            if (missing is null) return true;

            MetalDormancy.ThrowIfRequired(missing);
            _output.WriteLine("dormant: this machine cannot run the native Metal backend (" + missing + ").");
            return false;
        }
    }
}

using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE HALF OF THE REAL EMITTER THAT CAN BE EXECUTED WITHOUT A DEVICE: which view a bound resource offers a
    /// register file, which object carries a framebuffer's render targets, and how a span of engine binds is
    /// transposed into the parallel arrays <c>*SetConstantBuffers1</c> takes.
    /// <para>
    /// This is where issue #454's emitter is actually tested on this machine. The Vortice bodies cannot run here
    /// and arrive with the WARP leg, so everything that CAN be pulled out of them was: the resolution and the
    /// transposition live in <see cref="D3D11BindResolve"/>, which names no Direct3D type, and the emitter is
    /// left with a cast and a call. The rest of this file is structural, and that is deliberate rather than a
    /// consolation: the emitter's SHAPE is what the seam's rules are about, and the shape is checkable.
    /// </para>
    /// </summary>
    public sealed class D3D11EmitterResolutionTests
    {
        // ---- resource to view -------------------------------------------------------------------------------

        /// <summary>A null is a HOLE and passes through as one. An array bind covers a contiguous register span
        /// that may contain a register the set does not fill, and Direct3D 11 wants a null there.</summary>
        [Fact]
        public void ANullResource_ResolvesToAHoleRatherThanARefusal()
        {
            Assert.Null(D3D11BindResolve.ViewOf(null, D3D11RegisterFile.ShaderResource));
            Assert.Null(D3D11BindResolve.ViewOf(null, D3D11RegisterFile.ConstantBuffer));
        }

        /// <summary>
        /// A <see cref="GpuBufferRange"/> RESOLVES TO ITS BUFFER, which is the unwrapping a bind actually needs: a
        /// set stores the resource exactly as the caller bound it, so a structured buffer bound as a range arrives
        /// at the emitter as the range and not as the buffer. Missing this binds nothing and refuses nothing.
        /// </summary>
        [Fact]
        public void ABufferRange_ResolvesToTheBuffersOwnView()
        {
            var buffer = new ViewfulBuffer(shaderResource: new object());

            object? view = D3D11BindResolve.ViewOf(
                new GpuBufferRange(buffer, 0, 64), D3D11RegisterFile.ShaderResource);

            Assert.Same(((ID3D11BindableViews)buffer).ShaderResourceViewObject, view);
        }

        /// <summary>Each file reads its own member, so a texture bound at a sampler register is refused rather
        /// than binding the texture's shader resource view into the sampler file.</summary>
        [Fact]
        public void EachRegisterFile_ReadsItsOwnView()
        {
            var srv = new object();
            var uav = new object();
            var resource = new ViewfulBuffer(shaderResource: srv, unorderedAccess: uav);

            Assert.Same(srv, D3D11BindResolve.ViewOf(resource, D3D11RegisterFile.ShaderResource));
            Assert.Same(uav, D3D11BindResolve.ViewOf(resource, D3D11RegisterFile.UnorderedAccess));
        }

        /// <summary>
        /// A RESOURCE WITH NO VIEW FOR THE FILE IS A REFUSAL, and the message names the HLSL register letter so it
        /// can be matched against a shader. Views follow from the declared usage at creation, so this is the shape
        /// of "a layout element of the wrong kind" and of "a texture created without the usage bit its layout
        /// asks for", both of which would otherwise bind nothing the shader reads.
        /// </summary>
        [Fact]
        public void AResourceWithoutTheViewItsRegisterNeeds_IsRefusedByName()
        {
            var resource = new ViewfulBuffer(shaderResource: new object());

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => D3D11BindResolve.ViewOf(resource, D3D11RegisterFile.Sampler));

            Assert.Contains("'s' register", ex.Message, StringComparison.Ordinal);
            Assert.Contains("DECLARED", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>And a resource from another backend is refused by name rather than by an
        /// <see cref="InvalidCastException"/> out of the emitter.</summary>
        [Fact]
        public void AForeignResource_IsRefusedByName()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => D3D11BindResolve.ViewOf(new FakeSampler(), D3D11RegisterFile.Sampler));

            Assert.Contains(nameof(FakeSampler), ex.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(ID3D11BindableViews), ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A BUFFER FROM ANOTHER BACKEND IS REFUSED BY NAME, and refused AGAIN on the second bind of the same one,
        /// which is the half that was silent. The refusal is the RESOLVE, and the emitter runs it before the
        /// redundancy cache: guarding first would record the buffer, so the second identical
        /// <c>SetIndexBuffer</c> would compare equal against a cache describing a buffer the call never bound,
        /// pass silently, and leave the draw indexing whatever the input assembler still held.
        /// </summary>
        [Fact]
        public void AForeignIndexBuffer_IsRefusedByName_OnTheSecondBindToo()
        {
            var foreign = new FakeBuffer(64);

            ArgumentException first = Assert.Throws<ArgumentException>(
                () => D3D11BindResolve.NativeBuffer(foreign));
            ArgumentException second = Assert.Throws<ArgumentException>(
                () => D3D11BindResolve.NativeBuffer(foreign));

            Assert.Contains(nameof(FakeBuffer), first.Message, StringComparison.Ordinal);
            Assert.Equal(first.Message, second.Message);

            // The other half of the ordering rule, and the reason the resolve has to come first: the cache RECORDS
            // at the first ask, so a bind that resolved after it would find the second ask redundant.
            var streams = new D3D11VertexStreams();
            Assert.True(streams.BindIndexBuffer(foreign, GpuIndexFormat.UInt16));
            Assert.False(streams.BindIndexBuffer(foreign, GpuIndexFormat.UInt16));
        }

        // ---- the two framebuffer types --------------------------------------------------------------------

        /// <summary>
        /// BOTH FRAMEBUFFER TYPES ANSWER THE SAME SEAM, which is the swapchain review's finding made structural:
        /// an emitter that cast to one concrete type would work for every offscreen pass and throw on the first
        /// frame that renders to the window. <see cref="D3D11Framebuffer"/> is asserted by type rather than by
        /// instance because its constructor needs a real device.
        /// </summary>
        [Fact]
        public void BothFramebufferTypes_AnswerTheRenderTargetSeam()
        {
            Assert.True(typeof(ID3D11RenderTargetSurface).IsAssignableFrom(typeof(D3D11Framebuffer)));
            Assert.True(typeof(ID3D11RenderTargetSurface).IsAssignableFrom(typeof(D3D11SwapchainFramebuffer)));
        }

        /// <summary>The swapchain's wrapper is device-free, so its side of the seam is checked for real: one
        /// colour attachment, the CURRENT generation's views, and a depth attachment only when it has one.
        /// </summary>
        [Fact]
        public void TheSwapchainFramebuffer_AnswersItsCurrentGenerationsViews()
        {
            var first = new object();
            var second = new object();
            var framebuffer = new D3D11SwapchainFramebuffer(GpuPixelFormat.B8G8R8A8UNorm, null,
                new D3D11SwapchainAttachments(640, 480, first, null));

            var surface = (ID3D11RenderTargetSurface)framebuffer;
            Assert.Equal(1, surface.RenderTargetCount);
            Assert.Same(first, surface.RenderTargetAt(0));
            Assert.Null(surface.DepthStencil);

            framebuffer.Adopt(new D3D11SwapchainAttachments(320, 240, second, null));

            Assert.Same(second, surface.RenderTargetAt(0));
        }

        /// <summary>A framebuffer from another backend is refused by name, which is what a bind does with one
        /// rather than casting and throwing.</summary>
        [Fact]
        public void AForeignFramebuffer_IsRefusedByName()
        {
            var framebuffer = new FakeFramebuffer(
                new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm), 4, 4);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => D3D11BindResolve.RenderTargets(framebuffer));

            Assert.Contains(nameof(FakeFramebuffer), ex.Message, StringComparison.Ordinal);
        }

        // ---- the constant-buffer transposition ------------------------------------------------------------

        /// <summary>The windows come out as the two parallel <c>int</c> arrays the call takes, entry for entry, in
        /// register order.</summary>
        [Fact]
        public void TheConstantWindows_TransposeIntoTheParallelArrays()
        {
            var buffer = new ViewfulBuffer(new object());
            ReadOnlySpan<D3D11ConstantBufferBind> binds = new[]
            {
                new D3D11ConstantBufferBind(buffer, 16, 4),
                new D3D11ConstantBufferBind(buffer, 64, 16),
            };
            int[] first = new int[8];
            int[] counts = new int[8];

            D3D11BindResolve.Constants(binds, first, counts);

            Assert.Equal(new[] { 16, 64 }, first.Take(2));
            Assert.Equal(new[] { 4, 16 }, counts.Take(2));
        }

        /// <summary>A HOLE CARRIES ZERO AND ZERO, which Direct3D 11 requires of a null entry and rejects the whole
        /// call without, losing every other register in the same span with it.</summary>
        [Fact]
        public void AHoleInTheSpan_CarriesAZeroWindow()
        {
            ReadOnlySpan<D3D11ConstantBufferBind> binds = new D3D11ConstantBufferBind[2];
            int[] first = { 9, 9 };
            int[] counts = { 9, 9 };

            D3D11BindResolve.Constants(binds, first, counts);

            Assert.Equal(new[] { 0, 0 }, first);
            Assert.Equal(new[] { 0, 0 }, counts);
        }

        /// <summary>A window against no buffer is refused rather than zeroed, because it means something upstream
        /// built a bind wrongly and quietly fixing it hides that.</summary>
        [Fact]
        public void AWindowWithNoBuffer_IsRefused()
        {
            var binds = new[] { new D3D11ConstantBufferBind(null, 16, 4) };

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => D3D11BindResolve.Constants(binds, new int[4], new int[4]));

            Assert.Contains("null entry", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>A scratch shorter than the bind is a defect in the emitter rather than an oversized bind, and
        /// it says so instead of writing past the end.</summary>
        [Fact]
        public void AShortScratch_IsRefusedRatherThanOverrun()
        {
            var buffer = new ViewfulBuffer(new object());
            var binds = new[]
            {
                new D3D11ConstantBufferBind(buffer, 0, 16),
                new D3D11ConstantBufferBind(buffer, 16, 16),
            };

            Assert.Throws<ArgumentException>(
                () => D3D11BindResolve.Constants(binds, new int[1], new int[1]));
        }

        /// <summary>The scratch grows geometrically and never below eight, which is what "zero per-call
        /// allocation" means once a process is warm: a handful of reallocations over its lifetime rather than one
        /// per widening bind.</summary>
        [Theory]
        [InlineData(0, 8)]
        [InlineData(8, 8)]
        [InlineData(9, 16)]
        [InlineData(17, 32)]
        public void TheScratchCapacity_DoublesFromEight(int count, int expected)
            => Assert.Equal(expected, D3D11BindResolve.RoundedCapacity(count));

        /// <summary>
        /// A NON-ZERO SCISSOR INDEX IS REFUSED, in the one place both emitters ask, so the device-free trace
        /// cannot model an index the real call has no way to honour: <c>RSSetScissorRects</c> takes a count and
        /// always starts at rectangle 0. Every shipped call site passes zero.
        /// </summary>
        [Fact]
        public void ANonZeroScissorIndex_IsRefusedForBothEmitters()
        {
            var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), new D3D11NativeCallLog());
            emitter.Begin();

            D3D11BindResolve.RequireSingleScissorRect(0);
            Assert.Throws<ArgumentOutOfRangeException>(() => D3D11BindResolve.RequireSingleScissorRect(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => emitter.SetScissorRect(1, 0, 0, 8, 8));
        }

        // ---- the real emitter's shape -----------------------------------------------------------------------

        /// <summary>
        /// THE SHIPPED EMITTER IS IN THE SCANS THAT ENFORCE THE SEAM'S RULES. The readonly-struct check and issue
        /// #476's constructor check both walk every <see cref="ID3D11Emitter"/> implementation in the assembly, so
        /// they cover this type automatically. Asserted here anyway, because "automatically" is exactly the claim
        /// that stops being true when a type is moved or renamed, and those two scans pass vacuously over a set
        /// that no longer contains it.
        /// </summary>
        [Fact]
        public void TheRealEmitter_IsCoveredByTheSeamsShapeScans()
        {
            Type[] emitters = typeof(ID3D11Emitter).Assembly.GetTypes()
                .Where(t => typeof(ID3D11Emitter).IsAssignableFrom(t) && t != typeof(ID3D11Emitter))
                .ToArray();

            Assert.Contains(typeof(D3D11NativeEmitter), emitters);
            Assert.True(typeof(ID3D11BindSink).IsAssignableFrom(typeof(D3D11NativeEmitter)),
                "The real emitter must be its own bind sink, or the flush would have nowhere to put the array "
                + "calls decision R6 fans a resource set out into.");
        }

        /// <summary>
        /// IT RECEIVES THE DEVICE'S STATE AND ALLOCATES NEITHER OF ITS TWO CLASS REFERENCES (issue #476). Both
        /// constructor parameters are checked, not just the state one: an emitter that allocated its own
        /// <see cref="D3D11EmitterContext"/> would give every command list its own scratch arrays, which is
        /// harmless, and its own device context, which is not.
        /// </summary>
        [Fact]
        public void TheRealEmitter_TakesTheDeviceStateAndTheContextRatherThanBuildingThem()
        {
            ConstructorInfo[] constructors = typeof(D3D11NativeEmitter)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.All(constructors, c =>
            {
                Type[] parameters = c.GetParameters().Select(p => p.ParameterType).ToArray();
                Assert.Contains(typeof(D3D11DeviceState), parameters);
                Assert.Contains(typeof(D3D11EmitterContext), parameters);
            });
        }

        /// <summary>
        /// EVERY FIELD OF THE EMITTER IS A CLASS REFERENCE, which is the seam's readonly-struct rule and, here,
        /// also the load-path rule: the two scans above read <c>FieldType</c> and <c>ParameterType</c> on this
        /// type, and reading either RESOLVES it. A Vortice type in a field or a constructor parameter would
        /// therefore load the interop into the process from a test, on macOS, and take every load-path assertion
        /// in the run down with it. That is why the device context and the scratch arrays live behind
        /// <see cref="D3D11EmitterContext"/> instead of on the struct.
        /// </summary>
        [Fact]
        public void TheRealEmitterCarriesNoDirect3DTypeInItsFieldsOrItsConstructor()
        {
            Type[] surface = typeof(D3D11NativeEmitter)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.FieldType)
                .Concat(typeof(D3D11NativeEmitter)
                    .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(c => c.GetParameters().Select(p => p.ParameterType)))
                .ToArray();

            Assert.NotEmpty(surface);
            Assert.All(surface, t => Assert.False(
                (t.Assembly.GetName().Name ?? "").StartsWith("Vortice", StringComparison.Ordinal)
                    || (t.Assembly.GetName().Name ?? "").StartsWith("SharpGen", StringComparison.Ordinal),
                $"{t.Name} is a Direct3D interop type on the real emitter's field or constructor surface. The "
                + "seam's shape scans read those through reflection, so this would load the interop on a platform "
                + "that has none."));
        }

        /// <summary>
        /// THE CLAIM DECISION P1 RESTS ON, checked for everything this row added: exercising the whole device-free
        /// half of the draw path, and reflecting over the real emitter the way the seam's own scans do, must not
        /// put the Direct3D interop into the process on a platform that has none.
        /// </summary>
        [Fact]
        public void OffWindows_TheDrawPathAndTheEmittersShapePullInNoInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows it loads, by design

            var buffer = new ViewfulBuffer(new object(), new object());
            D3D11BindResolve.ViewOf(buffer, D3D11RegisterFile.ShaderResource);
            D3D11BindResolve.ViewOf(new GpuBufferRange(buffer, 0, 16), D3D11RegisterFile.ConstantBuffer);
            D3D11BindResolve.NativeBuffer(buffer);
            D3D11BindResolve.Constants(
                new[] { new D3D11ConstantBufferBind(buffer, 0, 16) }, new int[4], new int[4]);
            var swapchain = new D3D11SwapchainFramebuffer(
                GpuPixelFormat.B8G8R8A8UNorm, null, new D3D11SwapchainAttachments(8, 8, new object(), null));
            D3D11BindResolve.RenderTargets(swapchain);
            D3D11BindResolve.RequireColourAttachment(swapchain, 0);

            var streams = new D3D11VertexStreams();
            streams.AdoptStrides(new[] { 16u });
            streams.RecordVertexBuffer(0, buffer, 0);
            streams.BindIndexBuffer(buffer, GpuIndexFormat.UInt16);
            streams.TakeFlush(out _, out _);
            streams.Scrub(buffer, out _, out _);

            _ = typeof(D3D11NativeEmitter)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.FieldType)
                .ToArray();

            D3D11InteropLoad.AssertNotLoaded();
        }

        /// <summary>A bindable resource whose views are plain objects, which is all the resolution ever asks of
        /// one. It is a buffer as well, so a <see cref="GpuBufferRange"/> can be built over it.</summary>
        sealed class ViewfulBuffer : IGpuBuffer, ID3D11BindableViews
        {
            readonly object? _shaderResource;
            readonly object? _unorderedAccess;

            internal ViewfulBuffer(object? shaderResource = null, object? unorderedAccess = null)
            {
                _shaderResource = shaderResource;
                _unorderedAccess = unorderedAccess;
                Buffer = new object();
            }

            internal object Buffer { get; }

            public uint SizeInBytes => 1024;

            object? ID3D11BindableViews.ShaderResourceViewObject => _shaderResource;
            object? ID3D11BindableViews.UnorderedAccessViewObject => _unorderedAccess;
            object? ID3D11BindableViews.SamplerStateObject => null;
            object? ID3D11BindableViews.BufferObject => Buffer;

            public void Dispose()
            {
            }
        }
    }
}

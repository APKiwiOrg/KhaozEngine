using System;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>The Veldrid-backed <see cref="IGpuDevice"/> + <see cref="IGpuResourceFactory"/>. Wraps a
    /// <see cref="GraphicsDevice"/>; all Veldrid types stay confined here. The wrapped device is the same
    /// underlying object <see cref="GpuDeviceContext"/> owns, so the 2D and 3D renderers share one device.</summary>
    internal sealed class VeldridGpuDevice : IGpuDevice, IGpuResourceFactory, IGpuDeviceLifecycle
    {
        internal GraphicsDevice GraphicsDevice { get; }
        readonly bool _ownsDevice;
        // NOT readonly: re-wrapped on resize. D3D11/Vulkan rebuild the swapchain framebuffer (a new object) when
        // the swapchain resizes, so a wrapper cached once would dangle on the disposed old framebuffer.
        VeldridGpuFramebuffer? _swapchainFb;
        readonly VeldridGpuSampler _pointSampler;
        readonly VeldridGpuSampler _linearSampler;

        public GpuBackendKind Backend { get; }
        public GpuCapabilities Capabilities { get; }
        public IGpuResourceFactory Factory => this;
        public IGpuFramebuffer? SwapchainFramebuffer => _swapchainFb;
        public IGpuSampler PointSampler => _pointSampler;
        public IGpuSampler LinearSampler => _linearSampler;

        public VeldridGpuDevice(GraphicsDevice gd, GpuBackendKind backend, bool ownsDevice)
        {
            GraphicsDevice = gd;
            Backend = backend;
            _ownsDevice = ownsDevice;
            // One shared reader, so this copy and GpuDeviceContext.Capabilities cannot drift (they had: the
            // device name and the sampler-feature flags were populated there and dropped here).
            Capabilities = VeldridMap.ReadCapabilities(gd);
            // Wrap the device-owned swapchain framebuffer + shared samplers (no-dispose: the device owns them).
            _swapchainFb = gd.MainSwapchain != null
                ? new VeldridGpuFramebuffer(_liveness, gd.MainSwapchain.Framebuffer, ownsFramebuffer: false)
                : null;
            _pointSampler = new VeldridGpuSampler(_liveness, gd.PointSampler, ownsSampler: false);
            _linearSampler = new VeldridGpuSampler(_liveness, gd.LinearSampler, ownsSampler: false);
        }

        // Disposed latch: flipped (inside GpuDeviceContext's lifecycle gate) when the underlying device is
        // destroyed. Shared with every resource wrapper this device creates, so a straggling drain OR a
        // resource disposal from a wrapper that outlives the device (teardown-order hazard) no-ops instead of
        // calling into a dead device (the Vulkan loader aborts vkQueueWaitIdle and vkDestroy* against a
        // destroyed device, and device destruction already freed all child objects anyway).
        readonly DeviceLiveness _liveness = new();

        // Called by GpuDeviceContext.Dispose (inside the lifecycle gate) just before it destroys the device, via
        // IGpuDeviceLifecycle rather than a cast to this type, so a non-Veldrid device can come back through the
        // same creation path. Public because it implements an interface member: the class itself is internal, so
        // this is still assembly-scoped.
        public void MarkDeviceDisposed() => _liveness.MarkDead();

        // ---- IGpuDevice ----

        public void Submit(IGpuCommandList cl)
            => GraphicsDevice.SubmitCommands(((VeldridGpuCommandList)cl).CommandList);

        public void Submit(IGpuCommandList cl, IGpuFence fence)
            => GraphicsDevice.SubmitCommands(((VeldridGpuCommandList)cl).CommandList, ((VeldridGpuFence)fence).Fence);

        public void WaitForIdle()
        {
            if (_liveness.IsDead) return;   // a dead device has nothing to wait for (see the latch above)
            GraphicsDevice.WaitForIdle();
        }

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => GraphicsDevice.UpdateBuffer(((VeldridGpuBuffer)b).Buffer, offsetBytes, data);

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
            => GraphicsDevice.UpdateBuffer(((VeldridGpuBuffer)b).Buffer, offsetBytes, data);

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => GraphicsDevice.UpdateBuffer(((VeldridGpuBuffer)b).Buffer, offsetBytes, data);

        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => GraphicsDevice.UpdateTexture(((VeldridGpuTexture)texture).Texture, data, x, y, 0, width, height, 1, 0, 0);

        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mipLevel, uint arrayLayer)
            => GraphicsDevice.UpdateTexture(((VeldridGpuTexture)texture).Texture, data, x, y, 0, width, height, 1, mipLevel, arrayLayer);

        public MappedData Map(IGpuTexture staging, GpuMapMode mode)
        {
            MappedResource m = GraphicsDevice.Map(((VeldridGpuTexture)staging).Texture, VeldridMap.ToVeldrid(mode));
            return new MappedData(m.Data, m.RowPitch, m.SizeInBytes);
        }

        public void Unmap(IGpuTexture staging) => GraphicsDevice.Unmap(((VeldridGpuTexture)staging).Texture);

        public MappedData Map(IGpuBuffer staging, GpuMapMode mode)
        {
            MappedResource m = GraphicsDevice.Map(((VeldridGpuBuffer)staging).Buffer, VeldridMap.ToVeldrid(mode));
            return new MappedData(m.Data, m.RowPitch, m.SizeInBytes);
        }

        public void Unmap(IGpuBuffer staging) => GraphicsDevice.Unmap(((VeldridGpuBuffer)staging).Buffer);

        // Mirrors the requested vsync so the getter/setter round-trip on a headless (no-swapchain) device, where
        // Veldrid THROWS from GraphicsDevice.SyncToVerticalBlank. Seeded to Veldrid's default (true).
        bool _syncToVerticalBlank = true;

        public bool SyncToVerticalBlank
        {
            // On a windowed device Veldrid's GraphicsDevice.SyncToVerticalBlank propagates to the main swapchain in
            // place (each backend's SyncToVerticalBlankCore updates MainSwapchain.SyncToVerticalBlank), so vsync flips
            // without recreating the swapchain; on Metal this reaches CAMetalLayer.displaySyncEnabled. Veldrid throws
            // when there is no main swapchain (headless), so guard on it and fall back to the mirrored value.
            get => GraphicsDevice.MainSwapchain != null ? GraphicsDevice.SyncToVerticalBlank : _syncToVerticalBlank;
            set
            {
                _syncToVerticalBlank = value;
                if (GraphicsDevice.MainSwapchain != null) GraphicsDevice.SyncToVerticalBlank = value;
            }
        }

        public void ResizeSwapchain(uint w, uint h)
        {
            var sc = GraphicsDevice.MainSwapchain;
            if (sc == null) return;
            sc.Resize(w, h);
            // D3D11/Vulkan dispose the old swapchain framebuffer and build a new one on resize; Metal keeps the
            // same framebuffer object and resolves a fresh drawable each frame. Re-wrap only on a real object
            // change so SwapchainFramebuffer never hands back a disposed framebuffer (the Windows black-screen
            // after going fullscreen / maximising / drag-resizing), and Metal keeps its stable wrapper.
            if (!ReferenceEquals(_swapchainFb?.Framebuffer, sc.Framebuffer))
                _swapchainFb = new VeldridGpuFramebuffer(_liveness, sc.Framebuffer, ownsFramebuffer: false);
        }

        bool _capturing;
        string _capturePath = "";

        public void Present()
        {
            // Bracket a WHOLE frame for an armed Metal GPU capture: a frame's GPU work spans several Submits
            // (the offscreen mesh/skinned render-to-texture, then the 2D/composite pass) and ends at this present.
            // Wrapping a single Submit caught the wrong command buffer. Instead start the capture at one present
            // and stop at the next, so every command buffer of the intervening frame (incl. the skinned pass) is
            // recorded. Decision is pure + headless-testable, and the Metal interop runs only when it says to.
            //
            // The gate is the Veldrid Metal kind ALONE, not the IsMetal family, and GpuFrameCapture.VeldridPathCaptures
            // carries the reasoning: this code is inside the Veldrid wrapper, which a provider-built native device
            // never becomes, so widening it here would service nothing. MetalNative gets its own capture path.
            bool consume = false;
            if (!_capturing && GpuFrameCapture.VeldridPathCaptures(Backend) && GpuFrameCapture.TryConsume(out string p))
            {
                consume = true;
                _capturePath = p;
            }
            var action = GpuFrameCapture.NextAction(_capturing, consume);

            if (GraphicsDevice.MainSwapchain != null)
                GraphicsDevice.SwapBuffers(GraphicsDevice.MainSwapchain);

            switch (action)
            {
                case GpuFrameCapture.CaptureAction.StartAfterPresent:
                    // THE QUEUE COMES OUT OF VELDRID BY REFLECTION, and only on this path (M-G5). The native
                    // Metal backend owns its queue and hands the pointer in directly, which is why the read is
                    // its own named type rather than a step inside the capture.
                    _capturing = MetalFrameCapture.Start(
                        VeldridMetalCommandQueue.TryRead(GraphicsDevice), _capturePath);
                    break;
                case GpuFrameCapture.CaptureAction.StopAfterPresent:
                    // The drain travels in, because the capture no longer holds anything it could drain itself.
                    MetalFrameCapture.Stop(GraphicsDevice.WaitForIdle);
                    _capturing = false;
                    break;
            }
        }

        // ---- IGpuResourceFactory ----

        public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
        {
            // Structured buffers are always created RAW on Direct3D11. The engine's only shader path is
            // GLSL 450 -> SPIR-V -> SPIRV-Cross, and SPIRV-Cross emits every GLSL storage block (`buffer { T x[]; }`)
            // as a ByteAddressBuffer / RWByteAddressBuffer, never a StructuredBuffer<T>. A ByteAddressBuffer needs
            // a RAW view (R32_Typeless + the Raw view flag), which is what Veldrid's rawBuffer flag selects; the
            // default structured view would not match the shader. Since the shader shape is fixed by the pipeline,
            // this has exactly one correct value and is not worth a caller-visible knob. No-op on Metal and Vulkan.
            bool structured = (d.Usage & (GpuBufferUsage.StructuredBufferReadOnly | GpuBufferUsage.StructuredBufferReadWrite)) != 0;
            var desc = new BufferDescription(d.SizeInBytes, VeldridMap.ToVeldrid(d.Usage), d.StructureByteStride, structured);
            return new VeldridGpuBuffer(_liveness, GraphicsDevice.ResourceFactory.CreateBuffer(desc));
        }

        public IGpuTexture CreateTexture(in GpuTextureDescription d)
            => new VeldridGpuTexture(_liveness, GraphicsDevice.ResourceFactory.CreateTexture(new TextureDescription(
                d.Width, d.Height, 1, d.MipLevels, VeldridArrayLayers(d),
                VeldridMap.ToVeldrid(d.Format), VeldridMap.ToVeldrid(d.Usage), TextureType.Texture2D,
                VeldridMap.ToVeldrid(d.SampleCount))));

        /// <summary>
        /// THE ONE BACKEND THAT CANNOT SAY "ARRAY OF ONE", so it says "array of two" and never addresses the
        /// second slice (#666).
        ///
        /// <para><b>Veldrid 4.9.103 derives array-ness from the layer count in all three of its own backends and
        /// exposes no way to override it.</b> <c>MTLFormats.VdToMTLTextureType</c> picks <c>Type2DArray</c> only
        /// for <c>arrayLayers &gt; 1</c>, <c>VkTextureView</c> picks <c>Image2DArray</c> on the same test, and
        /// <c>D3D11TextureView</c> picks <c>Texture2DArray</c> on <c>d3dTex.ArrayLayers == 1</c>. There is no
        /// <c>TextureType</c> value and no <c>TextureUsage</c> bit for it, and a <c>TextureView</c> cannot widen
        /// it either, because its own <c>ArrayLayers</c> feeds the same comparison. So a one-layer array is
        /// UNREPRESENTABLE in the incumbent, and this padding is the whole of the workaround.</para>
        ///
        /// <para><b>The phantom slice is created and nothing else.</b> It is never uploaded to, never sampled and
        /// never named by a slot, so it costs one slice of memory and cannot reach a picture: the description's
        /// LOGICAL layer count is still one, every caller addresses layer 0, and the goldens see the same texel
        /// they saw when <c>Scene3D.LoadTileGroundMaterial</c> did this padding for itself. It is deliberately not
        /// applied to a cubemap (Veldrid counts CUBES there, so a second one would be six real faces and
        /// <see cref="GpuTextureDescription.IsArray"/> does not claim the cube case anyway) nor to a multisampled
        /// texture (which has no array type on this seam).</para>
        ///
        /// <para><b>The fork change that would remove this</b> is a <c>bool</c> on Veldrid's own
        /// <c>TextureDescription</c> (or <c>TextureUsage</c> bit 7, the single free bit in that
        /// <c>byte</c>-backed flags enum) carried into <c>MTLTexture</c>, <c>VkTextureView</c> and
        /// <c>D3D11TextureView</c> beside each <c>ArrayLayers &gt; 1</c> test. The three NATIVE backends already
        /// carry the seam's flag with no fork at all, so the incumbent is the only place a one-layer array is
        /// emulated rather than created.</para>
        /// </summary>
        static uint VeldridArrayLayers(in GpuTextureDescription d)
            => d.IsArray && d.ArrayLayers <= 1
                && (d.Usage & GpuTextureUsage.Cubemap) == 0 && d.SampleCount <= 1
                ? 2
                : d.ArrayLayers;

        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
        {
            Texture? d = depth != null ? ((VeldridGpuTexture)depth).Texture : null;
            var c = new Texture[colour.Length];
            for (int i = 0; i < colour.Length; i++) c[i] = ((VeldridGpuTexture)colour[i]).Texture;
            return new VeldridGpuFramebuffer(_liveness,
                GraphicsDevice.ResourceFactory.CreateFramebuffer(new FramebufferDescription(d, c)));
        }

        public IGpuSampler CreateSampler(in GpuSamplerDescription d)
        {
            // Anisotropic requires device support; fall back to trilinear so the splat-terrain sampler still
            // runs on a backend that lacks it (the path degrades, it does not break).
            var filter = d.Filter;
            uint maxAniso = d.MaximumAnisotropy;
            if (filter == GpuSamplerFilter.Anisotropic && !GraphicsDevice.Features.SamplerAnisotropy)
            {
                filter = GpuSamplerFilter.MinLinearMagLinearMipLinear;
                maxAniso = 0;
            }
            // LOD bias is a D3D11 / Vulkan feature; Metal's sampler has none and Veldrid THROWS rather than ignoring
            // a non-zero bias. Drop it to 0 when unsupported so the sampler still builds (it just misses the extra
            // distance-blur on that backend), mirroring the anisotropy fallback above.
            int lodBias = GraphicsDevice.Features.SamplerLodBias ? d.MipLodBias : 0;
            var desc = new SamplerDescription(
                VeldridMap.ToVeldrid(d.AddressModeU), VeldridMap.ToVeldrid(d.AddressModeV), VeldridMap.ToVeldrid(d.AddressModeW),
                VeldridMap.ToVeldrid(filter), null, maxAniso, 0, uint.MaxValue, lodBias, SamplerBorderColor.TransparentBlack);
            return new VeldridGpuSampler(_liveness, GraphicsDevice.ResourceFactory.CreateSampler(desc));
        }

        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
        {
            var src = d.Elements ?? Array.Empty<GpuResourceLayoutElement>();
            var elems = new ResourceLayoutElementDescription[src.Length];
            for (int i = 0; i < elems.Length; i++)
            {
                var e = src[i];
                var options = e.Dynamic ? ResourceLayoutElementOptions.DynamicBinding : ResourceLayoutElementOptions.None;
                elems[i] = new ResourceLayoutElementDescription(e.Name, VeldridMap.ToVeldrid(e.Kind), VeldridMap.ToVeldrid(e.Stages), options);
            }
            return new VeldridGpuResourceLayout(_liveness,
                GraphicsDevice.ResourceFactory.CreateResourceLayout(new ResourceLayoutDescription(elems)));
        }

        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
        {
            var bound = new BindableResource[d.Resources.Length];
            for (int i = 0; i < bound.Length; i++)
                bound[i] = ToVeldridBindable(d.Resources[i]);
            var desc = new ResourceSetDescription(((VeldridGpuResourceLayout)d.Layout).Layout, bound);
            return new VeldridGpuResourceSet(_liveness, GraphicsDevice.ResourceFactory.CreateResourceSet(desc));
        }

        static BindableResource ToVeldridBindable(IGpuBindableResource r) => r switch
        {
            VeldridGpuBuffer b => b.Buffer,
            VeldridGpuTexture t => t.Texture,
            VeldridGpuSampler s => s.Sampler,
            GpuBufferRange br => new DeviceBufferRange(((VeldridGpuBuffer)br.Buffer).Buffer, br.Offset, br.Size),
            _ => throw new ArgumentException($"Unsupported bindable resource: {r?.GetType().Name ?? "null"}"),
        };

        /// <summary>
        /// A one-line rendering of the option set BOTH of this device's shader paths compile under, which is the
        /// library's own defaults and deliberately not <c>SpirvFrontEndPin</c>. It is the memo key's options
        /// component (#640), and it is a literal here rather than a value read off Veldrid because the whole point
        /// of the separation is that this wrapper's defaults are maintained by the library and the engine's are
        /// maintained by the pin. Their equality is asserted by <c>VulkanSpirvIncumbentParityTests</c>, and this
        /// string is what keeps a divergence producing two cache entries rather than one wrong answer.
        /// </summary>
        const string VeldridDefaultsIdentity = "veldrid/spirv-defaults;debug=0;macros=0;entryPoint=main";

        // THE GLSL IS COMPILED HERE INSTEAD OF INSIDE CreateFromSpirv (#640), which is the same move
        // CreateComputeShaderFromSpirv below already makes and for a neighbouring reason. That one needed the
        // module so it could read the workgroup size back, and this one needs it so the memo has something to
        // hold. Worth 2515 ms of a 2560 ms Scene3D constructor on Metal, and the same order everywhere else,
        // because that constructor asks for 34 shader sets and every one of them was a fresh glslang run.
        //
        // WHY THE REROUTED BYTES ARE THE SAME BYTES, read off Veldrid.SPIRV 1.0.15 rather than assumed.
        // CreateFromSpirv has two branches and both sniff the SPIR-V magic before compiling anything, so a module
        // passed in is passed through either way. It is NOT EnsureSpirv that runs on the backends this actually
        // reaches: that member is the Vulkan branch only. On Direct3D 11 and Metal the compile lives inside
        // SpirvCompilation.CompileVertexFragment, per stage, behind a Util.HasSpirvHeader check, and it is issued
        // as CompileGlslToSpirv(..., string.Empty, stage, debug: target == GLSL || target == ESSL, 0, null).
        // For an HLSL or MSL target that debug flag is FALSE, the macro count is zero, and string.Empty resolves
        // to the same <veldrid-spirv-input> diagnostic name a null does (CompileGlslToSpirv substitutes it for
        // either), which is every input to the compile the call below makes under GlslCompileOptions.Default. So
        // the bytes match, and the cross-compile and shader creation past that point are untouched.
        //
        // THE ONE TARGET WHERE THEY WOULD NOT MATCH IS OPENGL, and it is unreachable. For a GLSL or ESSL target
        // that same expression compiles with debug: true, and SPIRV-Cross derives GL resource NAMES from the
        // debug information, so rerouting through the line below would drop them. GpuDeviceContext refuses an
        // OpenGL device on both the windowed and the headless path with NotSupportedException, so no call can
        // arrive on that target today. If one ever can, this reroute must pass debug: true for it or the names
        // break. HlslCrossCompilePin records the decompiled shape of the overload this rides on.
        static byte[] ToSpirv(string glsl, GpuShaderStages stage) =>
            SpirvCompileCache.Shared.GetOrCompile(VeldridDefaultsIdentity, stage, glsl,
                () => SpirvCompilation.CompileGlslToSpirv(
                    glsl, null, VeldridMap.ToVeldrid(stage), GlslCompileOptions.Default).SpirvBytes);

        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
        {
            Shader[] shaders = GraphicsDevice.ResourceFactory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, ToSpirv(vertGlsl, GpuShaderStages.Vertex), "main"),
                new ShaderDescription(ShaderStages.Fragment, ToSpirv(fragGlsl, GpuShaderStages.Fragment), "main"));
            return new VeldridGpuShaderSet(_liveness, shaders);
        }

        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl)
        {
            RequireCompute();
            // Compile the GLSL to SPIR-V here rather than letting CreateFromSpirv do it, so the module can be read
            // for its workgroup size before it is handed on (Veldrid.SPIRV never reports the size back). Passing
            // SPIR-V bytes through is exactly what CreateFromSpirv does with GLSL anyway - it sniffs the magic and
            // shaderc-compiles when absent.
            byte[] spirv;
            try
            {
                // Memoized on the same terms as the graphics pair above (#640). The diagnostic file name is fixed
                // for every compute source, so the stage alone separates these entries from that path's.
                spirv = SpirvCompileCache.Shared.GetOrCompile(
                    VeldridDefaultsIdentity, GpuShaderStages.Compute, computeGlsl,
                    () => SpirvCompilation.CompileGlslToSpirv(
                        computeGlsl, "compute", ShaderStages.Compute, GlslCompileOptions.Default).SpirvBytes);
            }
            catch (Exception ex)
            {
                throw new ShaderValidationException($"compute: GLSL -> SPIR-V compile failed: {ex.Message}", ex);
            }

            (uint gx, uint gy, uint gz) = SpirvLocalSize.Parse(spirv, "compute");

            // The single-stage CreateFromSpirv overload cross-compiles to the backend's compute shading language.
            // The graphics-only CrossCompileOptions (InvertVertexOutputY, FixClipSpaceZ) have no meaning for a
            // compute stage, so the defaults are correct here.
            Shader shader = GraphicsDevice.ResourceFactory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Compute, spirv, "main"));
            return new VeldridGpuComputeShader(_liveness, shader, gx, gy, gz);
        }

        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
        {
            var attachments = new BlendAttachmentDescription[d.BlendAttachments.Length];
            for (int i = 0; i < attachments.Length; i++)
                attachments[i] = VeldridMap.ToVeldrid(d.BlendAttachments[i]);
            var blend = new BlendStateDescription(
                new RgbaFloat(d.BlendFactor.X, d.BlendFactor.Y, d.BlendFactor.Z, d.BlendFactor.W), attachments);

            var depth = new DepthStencilStateDescription(
                d.DepthStencil.DepthTestEnabled, d.DepthStencil.DepthWriteEnabled, VeldridMap.ToVeldrid(d.DepthStencil.Comparison));

            var raster = new RasterizerStateDescription(
                VeldridMap.ToVeldrid(d.Rasterizer.CullMode), VeldridMap.ToVeldrid(d.Rasterizer.FillMode),
                VeldridMap.ToVeldrid(d.Rasterizer.FrontFace), d.Rasterizer.DepthClipEnabled, d.Rasterizer.ScissorTestEnabled);

            int n = d.VertexLayouts?.Count ?? 0;
            var vls = new VertexLayoutDescription[n];
            for (int i = 0; i < n; i++)
                vls[i] = ToVeldridVertexLayout(d.VertexLayouts![i]);

            var layouts = new ResourceLayout[d.ResourceLayouts.Length];
            for (int i = 0; i < layouts.Length; i++)
                layouts[i] = ((VeldridGpuResourceLayout)d.ResourceLayouts[i]).Layout;

            var pd = new GraphicsPipelineDescription
            {
                BlendState = blend,
                DepthStencilState = depth,
                RasterizerState = raster,
                PrimitiveTopology = VeldridMap.ToVeldrid(d.Topology),
                ResourceLayouts = layouts,
                ShaderSet = new ShaderSetDescription(vls, ((VeldridGpuShaderSet)d.ShaderSet).Shaders),
                Outputs = VeldridMap.ToVeldrid(d.Outputs),
            };
            return new VeldridGpuPipeline(_liveness, GraphicsDevice.ResourceFactory.CreateGraphicsPipeline(pd));
        }

        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
        {
            RequireCompute();
            var layouts = new ResourceLayout[d.ResourceLayouts.Length];
            for (int i = 0; i < layouts.Length; i++)
                layouts[i] = ((VeldridGpuResourceLayout)d.ResourceLayouts[i]).Layout;

            // Metal is the only backend that reads ThreadGroupSize* (it becomes threadsPerThreadgroup at dispatch
            // encode); Vulkan and D3D11 take the size from the shader module. Feeding it from the module's own
            // declaration means the two can never disagree.
            var shader = (VeldridGpuComputeShader)d.Shader;
            var pd = new ComputePipelineDescription(shader.Shader, layouts,
                shader.ThreadGroupSizeX, shader.ThreadGroupSizeY, shader.ThreadGroupSizeZ);
            return new VeldridGpuComputePipeline(_liveness, GraphicsDevice.ResourceFactory.CreateComputePipeline(pd));
        }

        // Fail at creation with a readable message rather than deep inside the backend at dispatch time. Callers
        // gate on GpuCapabilities.SupportsCompute; this is the backstop for the ones that forgot.
        void RequireCompute()
        {
            if (!GraphicsDevice.Features.ComputeShader)
                throw new NotSupportedException(
                    $"The {Backend} device does not support compute shaders. Gate on GpuCapabilities.SupportsCompute " +
                    "and fall back to a non-compute path.");
        }

        static VertexLayoutDescription ToVeldridVertexLayout(in GpuVertexLayoutDescription vl)
        {
            var elems = new VertexElementDescription[vl.Elements.Length];
            for (int i = 0; i < elems.Length; i++)
            {
                var e = vl.Elements[i];
                elems[i] = new VertexElementDescription(e.Name, VertexElementSemantic.TextureCoordinate, VeldridMap.ToVeldrid(e.Format));
            }
            // Stride 0 => let Veldrid compute from the elements; nonzero stride + step rate preserved for the
            // per-instance buffer (the 3D model pass uses stride + instanceStepRate 1).
            if (vl.Stride == 0 && vl.InstanceStepRate == 0)
                return new VertexLayoutDescription(elems);
            return new VertexLayoutDescription(vl.Stride, vl.InstanceStepRate, elems);
        }

        public IGpuCommandList CreateCommandList()
            => new VeldridGpuCommandList(_liveness, GraphicsDevice.ResourceFactory.CreateCommandList());

        public IGpuFence CreateFence()
        {
            // Gate here rather than letting the caller hold a fence that signals on something other than GPU
            // completion. Every backend HAS a Veldrid Fence, but only two of them signal it from the GPU (see
            // VeldridMap.SupportsCompletionFences), and the difference is invisible at the call site.
            if (!Capabilities.SupportsCompletionFences)
                throw new NotSupportedException(
                    $"The {Backend} device has no GPU-completion fence (its Veldrid fence is signaled by the submit " +
                    "call itself, not by the GPU). Gate on GpuCapabilities.SupportsCompletionFences and fall back to " +
                    "WaitForIdle.");
            return new VeldridGpuFence(_liveness, GraphicsDevice.ResourceFactory.CreateFence(signaled: false));
        }

        public void Dispose()
        {
            if (_ownsDevice) { _liveness.MarkDead(); GraphicsDevice.Dispose(); }
        }
    }
}

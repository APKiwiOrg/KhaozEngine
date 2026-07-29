using System;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>The Veldrid-backed <see cref="IGpuDevice"/> + <see cref="IGpuResourceFactory"/>. Wraps a
    /// <see cref="GraphicsDevice"/>; all Veldrid types stay confined here. The wrapped device is the same
    /// underlying object <see cref="GpuDeviceContext"/> owns, so the 2D and 3D renderers share one device.</summary>
    internal sealed class VeldridGpuDevice : IGpuDevice, IGpuResourceFactory
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

        // Called by GpuDeviceContext.Dispose (inside the lifecycle gate) just before it destroys the device.
        internal void MarkDeviceDisposed() => _liveness.Dead = true;

        // ---- IGpuDevice ----

        public void Submit(IGpuCommandList cl)
            => GraphicsDevice.SubmitCommands(((VeldridGpuCommandList)cl).CommandList);

        public void Submit(IGpuCommandList cl, IGpuFence fence)
            => GraphicsDevice.SubmitCommands(((VeldridGpuCommandList)cl).CommandList, ((VeldridGpuFence)fence).Fence);

        public void WaitForIdle()
        {
            if (_liveness.Dead) return;   // a dead device has nothing to wait for (see the latch above)
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
            // Wrapping a single Submit caught the wrong command buffer; instead start the capture at one present
            // and stop at the next, so every command buffer of the intervening frame (incl. the skinned pass) is
            // recorded. Decision is pure + headless-testable; the Metal interop runs only when it says to.
            bool consume = false;
            if (!_capturing && Backend == GpuBackendKind.Metal && GpuFrameCapture.TryConsume(out string p))
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
                    _capturing = MetalFrameCapture.Start(GraphicsDevice, _capturePath);
                    break;
                case GpuFrameCapture.CaptureAction.StopAfterPresent:
                    MetalFrameCapture.Stop(GraphicsDevice);
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
                d.Width, d.Height, 1, d.MipLevels, d.ArrayLayers,
                VeldridMap.ToVeldrid(d.Format), VeldridMap.ToVeldrid(d.Usage), TextureType.Texture2D,
                VeldridMap.ToVeldrid(d.SampleCount))));

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

        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
        {
            Shader[] shaders = GraphicsDevice.ResourceFactory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(vertGlsl), "main"),
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(fragGlsl), "main"));
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
                spirv = SpirvCompilation.CompileGlslToSpirv(
                    computeGlsl, "compute", ShaderStages.Compute, GlslCompileOptions.Default).SpirvBytes;
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
            if (_ownsDevice) { _liveness.Dead = true; GraphicsDevice.Dispose(); }
        }
    }
}

using System;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>The Veldrid-backed <see cref="IGpuDevice"/> + <see cref="IGpuResourceFactory"/>. Wraps a
    /// <see cref="GraphicsDevice"/>; all Veldrid types stay confined here. The wrapped device is the SAME
    /// underlying object exposed transitionally by <see cref="GpuDeviceContext.Device"/>, so 2D (migrated) and
    /// 3D (still raw until phase 3c) share one device.</summary>
    internal sealed class VeldridGpuDevice : IGpuDevice, IGpuResourceFactory
    {
        internal GraphicsDevice GraphicsDevice { get; }
        readonly bool _ownsDevice;
        readonly VeldridGpuFramebuffer? _swapchainFb;
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
            Capabilities = new GpuCapabilities(gd.IsClipSpaceYInverted, gd.IsDepthRangeZeroToOne);
            // Wrap the device-owned swapchain framebuffer + shared samplers (no-dispose: the device owns them).
            _swapchainFb = gd.MainSwapchain != null
                ? new VeldridGpuFramebuffer(gd.MainSwapchain.Framebuffer, ownsFramebuffer: false)
                : null;
            _pointSampler = new VeldridGpuSampler(gd.PointSampler, ownsSampler: false);
            _linearSampler = new VeldridGpuSampler(gd.LinearSampler, ownsSampler: false);
        }

        // ---- IGpuDevice ----

        public void Submit(IGpuCommandList cl)
            => GraphicsDevice.SubmitCommands(((VeldridGpuCommandList)cl).CommandList);

        public void WaitForIdle() => GraphicsDevice.WaitForIdle();

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => GraphicsDevice.UpdateBuffer(((VeldridGpuBuffer)b).Buffer, offsetBytes, data);

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
            => GraphicsDevice.UpdateBuffer(((VeldridGpuBuffer)b).Buffer, offsetBytes, data);

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => GraphicsDevice.UpdateBuffer(((VeldridGpuBuffer)b).Buffer, offsetBytes, data);

        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => GraphicsDevice.UpdateTexture(((VeldridGpuTexture)texture).Texture, data, x, y, 0, width, height, 1, 0, 0);

        public MappedData Map(IGpuTexture staging, GpuMapMode mode)
        {
            MappedResource m = GraphicsDevice.Map(((VeldridGpuTexture)staging).Texture, VeldridMap.ToVeldrid(mode));
            return new MappedData(m.Data, m.RowPitch, m.SizeInBytes);
        }

        public void Unmap(IGpuTexture staging) => GraphicsDevice.Unmap(((VeldridGpuTexture)staging).Texture);

        public void ResizeSwapchain(uint w, uint h) => GraphicsDevice.MainSwapchain?.Resize(w, h);

        public void Present()
        {
            if (GraphicsDevice.MainSwapchain != null)
                GraphicsDevice.SwapBuffers(GraphicsDevice.MainSwapchain);
        }

        // ---- IGpuResourceFactory ----

        public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
            => new VeldridGpuBuffer(GraphicsDevice.ResourceFactory.CreateBuffer(
                new BufferDescription(d.SizeInBytes, VeldridMap.ToVeldrid(d.Usage), d.StructureByteStride)));

        public IGpuTexture CreateTexture(in GpuTextureDescription d)
            => new VeldridGpuTexture(GraphicsDevice.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                d.Width, d.Height, d.MipLevels, d.ArrayLayers,
                VeldridMap.ToVeldrid(d.Format), VeldridMap.ToVeldrid(d.Usage))));

        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
        {
            Texture? d = depth != null ? ((VeldridGpuTexture)depth).Texture : null;
            var c = new Texture[colour.Length];
            for (int i = 0; i < colour.Length; i++) c[i] = ((VeldridGpuTexture)colour[i]).Texture;
            return new VeldridGpuFramebuffer(
                GraphicsDevice.ResourceFactory.CreateFramebuffer(new FramebufferDescription(d, c)));
        }

        public IGpuSampler CreateSampler(in GpuSamplerDescription d)
        {
            var desc = new SamplerDescription(
                VeldridMap.ToVeldrid(d.AddressModeU), VeldridMap.ToVeldrid(d.AddressModeV), VeldridMap.ToVeldrid(d.AddressModeW),
                VeldridMap.ToVeldrid(d.Filter), null, 0, 0, uint.MaxValue, 0, SamplerBorderColor.TransparentBlack);
            return new VeldridGpuSampler(GraphicsDevice.ResourceFactory.CreateSampler(desc));
        }

        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
        {
            var src = d.Elements ?? Array.Empty<GpuResourceLayoutElement>();
            var elems = new ResourceLayoutElementDescription[src.Length];
            for (int i = 0; i < elems.Length; i++)
            {
                var e = src[i];
                elems[i] = new ResourceLayoutElementDescription(e.Name, VeldridMap.ToVeldrid(e.Kind), VeldridMap.ToVeldrid(e.Stages));
            }
            return new VeldridGpuResourceLayout(
                GraphicsDevice.ResourceFactory.CreateResourceLayout(new ResourceLayoutDescription(elems)));
        }

        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
        {
            var bound = new BindableResource[d.Resources.Length];
            for (int i = 0; i < bound.Length; i++)
                bound[i] = ToVeldridBindable(d.Resources[i]);
            var desc = new ResourceSetDescription(((VeldridGpuResourceLayout)d.Layout).Layout, bound);
            return new VeldridGpuResourceSet(GraphicsDevice.ResourceFactory.CreateResourceSet(desc));
        }

        static BindableResource ToVeldridBindable(IGpuBindableResource r) => r switch
        {
            VeldridGpuBuffer b => b.Buffer,
            VeldridGpuTexture t => t.Texture,
            VeldridGpuSampler s => s.Sampler,
            _ => throw new ArgumentException($"Unsupported bindable resource: {r?.GetType().Name ?? "null"}"),
        };

        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
        {
            Shader[] shaders = GraphicsDevice.ResourceFactory.CreateFromSpirv(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(vertGlsl), "main"),
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(fragGlsl), "main"));
            return new VeldridGpuShaderSet(shaders);
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
            return new VeldridGpuPipeline(GraphicsDevice.ResourceFactory.CreateGraphicsPipeline(pd));
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
            => new VeldridGpuCommandList(GraphicsDevice.ResourceFactory.CreateCommandList());

        public void Dispose()
        {
            if (_ownsDevice) GraphicsDevice.Dispose();
        }
    }
}

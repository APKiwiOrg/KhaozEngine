using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A device-free <see cref="IGpuDevice"/> for <see cref="GpuReadbackRing"/>. It reports completion fences,
    /// implements <see cref="IGpuFencedReadback"/>, hands out <see cref="ManualFence"/>s a test signals by hand, and
    /// maps a staging texture to pinned memory holding a per-texture byte pattern at a row pitch padded past the
    /// packed width, so a read that skips the de-stride lands the pad bytes in the destination. The draining
    /// <see cref="Map(IGpuTexture, GpuMapMode)"/> maps the same way but is counted apart, so a ring that reached
    /// for it instead of the fenced map shows up in <see cref="Maps"/>. Resources come from <see cref="FakeGpuDevice"/>'s factory, so
    /// whether the ring freed them is readable off <see cref="FakeTexture.Disposed"/>.
    /// </summary>
    internal sealed class ReadbackRingFakeDevice : IGpuDevice, IGpuFencedReadback
    {
        /// <summary>Bytes past <c>Width * 4</c> on every mapped row.</summary>
        internal const int RowPad = 16;

        /// <summary>The value every pad byte holds, so a destination carrying one is easy to spot.</summary>
        internal const byte PadByte = 0xEE;

        readonly FakeGpuDevice _inner = new();
        readonly RingFactory _factory;
        readonly Dictionary<IGpuTexture, byte[]> _mapped = new();

        internal ReadbackRingFakeDevice()
        {
            _factory = new RingFactory((FakeGpuResourceFactory)_inner.Factory);
            GpuCapabilities c = _inner.Capabilities;
            Capabilities = GpuCapabilities.FromSupportedMsaaSampleCounts(
                c.ClipSpaceYInverted, c.DepthRangeZeroToOne, c.SupportedMsaaSampleCounts, c.DeviceName,
                c.SamplerAnisotropy, c.SamplerLodBias, c.SupportsShadowMaps,
                supportsCompute: false, supportsCompletionFences: true);
        }

        /// <summary>Every texture the factory handed out, in creation order.</summary>
        internal List<FakeTexture> Textures => _factory.Inner.Textures;

        /// <summary>Every fence the factory handed out, in creation order.</summary>
        internal List<ManualFence> Fences => _factory.Fences;

        /// <summary>Every fence handed to a fenced submit, in submit order.</summary>
        internal List<IGpuFence> SubmittedFences { get; } = new();
        internal int SubmitCalls { get; private set; }

        /// <summary>Whole-texture copies recorded on every list this factory handed out.</summary>
        internal int CopyTextureCalls
        {
            get
            {
                int n = 0;
                foreach (CommandTallyGpuCommandList cl in _factory.CommandLists) n += cl.Tally[GpuCommandKind.CopyTexture];
                return n;
            }
        }

        internal int WaitForIdleCalls { get; private set; }

        /// <summary>Calls to the draining <see cref="Map(IGpuTexture, GpuMapMode)"/>. The ring never makes one.</summary>
        internal int Maps { get; private set; }

        /// <summary>Calls to <see cref="MapForRead"/> that mapped.</summary>
        internal int MapForReadCalls { get; private set; }

        internal int Unmaps { get; private set; }

        /// <summary>The texture the last map of either kind named.</summary>
        internal IGpuTexture? LastMapped { get; private set; }

        /// <summary>The bytes a de-strided read of <paramref name="texture"/> must produce: the mapped pattern at
        /// <c>Width * 4</c> bytes per row, pad absent.</summary>
        internal byte[] PackedPattern(IGpuTexture texture)
        {
            int w = (int)texture.Width, h = (int)texture.Height, seed = Textures.IndexOf((FakeTexture)texture);
            var packed = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int i = 0; i < w * 4; i++)
                    packed[y * w * 4 + i] = PatternByte(seed, y, i);
            return packed;
        }

        // Never PadByte for a small texture, so a pad byte in a destination is always a de-stride miss.
        static byte PatternByte(int seed, int y, int i) => (byte)((seed * 53 + y * 17 + i) % 200);

        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities { get; }
        public IGpuResourceFactory Factory => _factory;
        public IGpuFramebuffer? SwapchainFramebuffer => null;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;
        public bool SyncToVerticalBlank { get; set; }

        public void Submit(IGpuCommandList cl) => SubmitCalls++;
        public void Submit(IGpuCommandList cl, IGpuFence fence) { SubmitCalls++; SubmittedFences.Add(fence); }
        public void Submit(IGpuCommandList cl, ReadOnlySpan<IGpuFence> completions)
        {
            GpuCompletionFences.Validate(completions);
            Submit(cl);
            foreach (var completion in completions) SubmittedFences.Add(completion);
        }
        public void WaitForIdle() => WaitForIdleCalls++;

        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { }
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height) { }
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
        { }

        public MappedData Map(IGpuTexture staging, GpuMapMode mode)
        {
            Maps++;
            return MapPattern(staging);
        }

        // The interface's refusals, in its order: a texture that is not staging, then a fence not yet signaled.
        public MappedData MapForRead(IGpuTexture staging, IGpuFence fence)
        {
            if (staging is not FakeTexture { Usage: GpuTextureUsage.Staging })
                throw new ArgumentException("not a staging texture", nameof(staging));
            if (!fence.Signaled) throw new InvalidOperationException("the fence has not signaled");
            MapForReadCalls++;
            return MapPattern(staging);
        }

        MappedData MapPattern(IGpuTexture staging)
        {
            LastMapped = staging;
            int w = (int)staging.Width, h = (int)staging.Height, pitch = w * 4 + RowPad;
            int seed = Textures.IndexOf((FakeTexture)staging);
            byte[] memory = GC.AllocateArray<byte>(pitch * h, pinned: true);
            for (int y = 0; y < h; y++)
                for (int i = 0; i < pitch; i++)
                    memory[y * pitch + i] = i < w * 4 ? PatternByte(seed, y, i) : PadByte;
            _mapped[staging] = memory;
            return new MappedData(Marshal.UnsafeAddrOfPinnedArrayElement(memory, 0), (uint)pitch, (uint)memory.Length);
        }

        public void Unmap(IGpuTexture staging)
        {
            Unmaps++;
            _mapped.Remove(staging);
        }

        public MappedData Map(IGpuBuffer staging, GpuMapMode mode) => _inner.Map(staging, mode);
        public void Unmap(IGpuBuffer staging) { }

        public void ResizeSwapchain(uint w, uint h) { }
        public void Present() { }
        public void Dispose() => _inner.Dispose();

        /// <summary>Forwards to the fake factory, except that fences are <see cref="ManualFence"/>s and command
        /// lists count what they record.</summary>
        sealed class RingFactory : IGpuResourceFactory
        {
            internal RingFactory(FakeGpuResourceFactory inner) => Inner = inner;

            internal FakeGpuResourceFactory Inner { get; }
            internal List<ManualFence> Fences { get; } = new();
            internal List<CommandTallyGpuCommandList> CommandLists { get; } = new();

            public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => Inner.CreateBuffer(d);
            public IGpuTexture CreateTexture(in GpuTextureDescription d) => Inner.CreateTexture(d);
            public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
                => Inner.CreateFramebuffer(depth, colour);
            public IGpuSampler CreateSampler(in GpuSamplerDescription d) => Inner.CreateSampler(d);
            public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
                => Inner.CreateResourceLayout(d);
            public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => Inner.CreateResourceSet(d);
            public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
                => Inner.CreateShadersFromSpirv(vertGlsl, fragGlsl);
            public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => Inner.CreateGraphicsPipeline(d);
            public IGpuCommandList CreateCommandList()
            {
                var cl = new CommandTallyGpuCommandList(Inner.CreateCommandList());
                CommandLists.Add(cl);
                return cl;
            }
            public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl)
                => Inner.CreateComputeShaderFromSpirv(computeGlsl);
            public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
                => Inner.CreateComputePipeline(d);
            public IGpuFence CreateFence()
            {
                var fence = new ManualFence();
                Fences.Add(fence);
                return fence;
            }
        }
    }

    /// <summary>A fence the test signals by hand, so completion is asserted at an exact step instead of
    /// raced.</summary>
    internal sealed class ManualFence : IGpuFence
    {
        public bool Signaled { get; private set; }
        internal int Resets { get; private set; }
        internal bool Disposed { get; private set; }

        internal void Signal() => Signaled = true;

        public void Reset()
        {
            Signaled = false;
            Resets++;
        }

        public void Dispose() => Disposed = true;
    }
}

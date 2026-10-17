using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// A fullscreen linear downscale: one triangle that samples a source texture of any size and colour format over
    /// the whole of an <see cref="GpuPixelFormat.R8G8B8A8UNorm"/> destination. The frame capture path copies the
    /// swapchain image into a sampled texture and records this to shrink it to capture size before readback.
    /// <para>
    /// <b>RGBA OUT, WHATEVER THE SOURCE STORES.</b> Sampling returns logical RGBA for a
    /// <see cref="GpuPixelFormat.B8G8R8A8UNorm"/> texture as for an RGBA one, so the destination bytes are in RGBA
    /// order with no CPU swizzle, and alpha is forced to one.
    /// </para>
    /// <para>
    /// <b>A BOX FILTER UP TO FOUR TO ONE.</b> The fragment takes four bilinear taps a quarter of a destination
    /// texel either side of its centre on each axis, which covers a 4x4 source footprint. A larger reduction
    /// skips source texels between the taps.
    /// </para>
    /// <para>
    /// The sampler is this instance's own clamp-addressed <see cref="GpuSamplerDescription.Linear"/>, not the
    /// device's wrap-addressed <see cref="IGpuDevice.LinearSampler"/>, so a tap at the border never reads the
    /// opposite edge.
    /// </para>
    /// </summary>
    public sealed class GpuScaledCopy : IDisposable
    {
        /// <summary>Render3D's fullscreen triangle, copied verbatim because this package does not reference
        /// Render3D. It renders vertically flipped on every backend, which <see cref="FragmentSource"/>
        /// corrects.</summary>
        internal const string VertexSource = @"#version 450
layout(location=0) out vec2 vUv;
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vUv = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

        /// <summary>Four bilinear taps around the destination texel's centre, averaged, alpha forced to one. V is
        /// flipped first, because no final blit follows this pass to undo the fullscreen triangle's flip.</summary>
        internal const string FragmentSource = @"#version 450
layout(set=0, binding=0) uniform texture2D Src;
layout(set=0, binding=1) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
void main() {
    // Every fullscreen pass renders flipped on every backend (PixelPostProcess.cs:316). No final blit follows here, so flip V.
    vec2 uv = vec2(vUv.x, 1.0 - vUv.y);
    vec2 o = 0.25 * vec2(dFdx(uv.x), dFdy(uv.y));   // a quarter of one destination texel in source UV
    vec3 c = texture(sampler2D(Src, Samp), uv + vec2(-o.x, -o.y)).rgb
           + texture(sampler2D(Src, Samp), uv + vec2( o.x, -o.y)).rgb
           + texture(sampler2D(Src, Samp), uv + vec2(-o.x,  o.y)).rgb
           + texture(sampler2D(Src, Samp), uv + vec2( o.x,  o.y)).rgb;
    oColor = vec4(c * 0.25, 1.0);
}";

        readonly IGpuDevice _device;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        readonly IGpuSampler _sampler;
        readonly IGpuPipeline _pipeline;
        readonly List<IGpuResourceSet> _replaced = new();
        IGpuTexture? _setSource;
        IGpuResourceSet? _set;
        bool _disposed;

        /// <summary>Build the shaders, layout, sampler and pipeline once, for a single
        /// <see cref="GpuPixelFormat.R8G8B8A8UNorm"/> colour attachment and no depth.</summary>
        public GpuScaledCopy(IGpuDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            _device = device;
            IGpuResourceFactory f = device.Factory;
            _shaders = f.CreateShadersFromSpirv(VertexSource, FragmentSource);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Src", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            _sampler = f.CreateSampler(GpuSamplerDescription.Linear);
            _pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm),
            });
        }

        /// <summary>
        /// Record, into an OPEN recording, a linear-filtered draw of <paramref name="source"/> over the whole of
        /// <paramref name="destination"/>. The source may be any size and any colour format with
        /// <see cref="GpuTextureUsage.Sampled"/> usage. The destination must have exactly one
        /// <see cref="GpuPixelFormat.R8G8B8A8UNorm"/> colour attachment, single-sample, with no depth. Output is in
        /// RGBA order whatever the source's storage order, with alpha forced to one.
        /// <para>
        /// A source other than the last one recorded replaces this instance's cached resource set. The replaced
        /// set is kept alive until <see cref="Dispose"/>. Use the overload that takes a retire callback to free it
        /// sooner.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="destination"/> is not one single-sample
        /// <see cref="GpuPixelFormat.R8G8B8A8UNorm"/> attachment with no depth.</exception>
        /// <exception cref="ObjectDisposedException">This instance is disposed.</exception>
        public void Record(IGpuCommandList commands, IGpuTexture source, IGpuFramebuffer destination)
            => Record(commands, source, destination, retire: null);

        /// <summary>
        /// Record as <see cref="Record(IGpuCommandList, IGpuTexture, IGpuFramebuffer)"/> does, and hand a resource
        /// set this record replaces to <paramref name="retire"/>, which must destroy it only once the GPU has
        /// finished every submission that used it, the open recording's included. A frame capture tap passes its
        /// context's <c>Retire</c>. Null keeps the replaced set alive until <see cref="Dispose"/>.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="destination"/> is not one single-sample
        /// <see cref="GpuPixelFormat.R8G8B8A8UNorm"/> attachment with no depth.</exception>
        /// <exception cref="ObjectDisposedException">This instance is disposed.</exception>
        public void Record(IGpuCommandList commands, IGpuTexture source, IGpuFramebuffer destination,
            Action<IDisposable>? retire)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(commands);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            RequireRgba8Destination(destination);

            commands.SetFramebuffer(destination);
            commands.SetFullScissorRects();
            commands.SetPipeline(_pipeline);
            commands.SetGraphicsResourceSet(0, SetFor(source, retire));
            commands.Draw(3);
        }

        // One set, over the last source drawn. A different source instance replaces it, and the replaced set is
        // NEVER disposed here. A draw already recorded in the open list may use it, and the GPU reads that list
        // only after its submit. A Vulkan set's deferred free is stamped with the timeline value already
        // allocated, which covers earlier submissions and not this recording, so freeing it now would let a
        // later present destroy a descriptor set a running draw still uses (VUID-vkFreeDescriptorSets-00309).
        // The set goes to the caller's completion-aware retire, or waits in _replaced until Dispose.
        IGpuResourceSet SetFor(IGpuTexture source, Action<IDisposable>? retire)
        {
            if (_set is not null && ReferenceEquals(_setSource, source)) return _set;
            if (_set is not null)
            {
                if (retire is not null) retire(_set);
                else _replaced.Add(_set);
            }
            _set = null;
            _setSource = null;
            _set = _device.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, source, _sampler));
            _setSource = source;
            return _set;
        }

        static void RequireRgba8Destination(IGpuFramebuffer destination)
        {
            GpuOutputDescription outputs = destination.Outputs;
            if (outputs.Colour.Length == 1 && outputs.Colour[0] == GpuPixelFormat.R8G8B8A8UNorm
                && outputs.Depth is null && outputs.SampleCount == 1)
            {
                return;
            }

            throw new ArgumentException(
                "GpuScaledCopy renders into exactly one single-sample R8G8B8A8UNorm colour attachment with no depth, "
                + "which is the one shape its pipeline is built for. This destination has "
                + outputs.Colour.Length.ToString(CultureInfo.InvariantCulture) + " colour attachment(s) ["
                + string.Join(", ", outputs.Colour) + "], depth " + (outputs.Depth?.ToString() ?? "none")
                + " and " + outputs.SampleCount.ToString(CultureInfo.InvariantCulture) + " sample(s).",
                nameof(destination));
        }

        /// <summary>Free the cached resource set, every replaced set no retire callback took, the pipeline, the
        /// layout, the shaders and the sampler. Call it once the GPU has finished every recording this instance
        /// drew into, as for any GPU resource.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (IGpuResourceSet replaced in _replaced) replaced.Dispose();
            _replaced.Clear();
            _set?.Dispose();
            _set = null;
            _setSource = null;
            _pipeline.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _sampler.Dispose();
        }
    }
}

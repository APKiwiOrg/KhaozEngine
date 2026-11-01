using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Mirrors the explicitly versioned primary-backend wire identities, using their pure vertex planners.
// No native object is reconstructed. Replay is the later cold owner's responsibility.
internal static class RendererPerfConstructionResolvedIdentity
{
    internal static string Layout(RendererPerfPipelineRecipe r, ImmutableArray<RendererPerfLayoutRecipe> layouts) => Hash(w =>
    {
        w.Write(r.LayoutIds.Length);
        foreach (int id in r.LayoutIds)
        {
            var elements = layouts[id].Elements; w.Write(elements.Length);
            foreach (var e in elements) { w.Write(e.Name); w.Write((int)e.Kind); w.Write((int)e.Stages); w.Write(e.Dynamic); }
        }
    });

    internal static string Pipeline(RendererPerfPipelineRecipe r, GpuPipelineConstructionIdentity identity, string layout, GpuBackendKind backend)
        => Hash(w =>
        {
            if (r.Kind == RendererPerfPipelineKind.Compute)
            {
                w.Write("compute,main,no-specialization"); w.Write(identity.ShaderArtifactSha256.Sha256!); w.Write(layout);
                w.Write(identity.LocalSizeX); w.Write(identity.LocalSizeY); w.Write(identity.LocalSizeZ); return;
            }
            var g = r.Graphics!;
            var vertices = g.VertexLayouts.Select(v => new GpuVertexLayoutDescription(v.Stride, v.InstanceStepRate, v.Elements.ToArray())).ToArray();
            bool vulkan = backend == GpuBackendKind.VulkanNative;
            w.Write(vulkan ? "graphics,vertex=main,fragment=main,no-specialization,dynamic=viewport+scissor"
                : "graphics,vertex=main,pixel=main,no-specialization,dynamic=viewport+scissor,stencil=disabled");
            if (vulkan) w.Write(identity.ShaderArtifactSha256.Sha256!);
            else { w.Write(identity.Stages[0].ShaderArtifactSha256.Sha256!); w.Write(identity.Stages[1].ShaderArtifactSha256.Sha256!); }
            w.Write(layout); w.Write((int)g.Topology);
            if (vulkan)
            {
                var bindings = VulkanVertexInput.Build(vertices, out var attributes);
                w.Write(bindings.Length);
                foreach (var b in bindings) { w.Write(b.Binding); w.Write(b.Stride); w.Write(b.PerInstance); }
                w.Write(attributes.Length);
                foreach (var a in attributes) { w.Write(a.Location); w.Write(a.Binding); w.Write((int)a.Format); w.Write(a.Offset); }
            }
            else
            {
                var elements = D3D11InputLayoutPlan.Build(vertices, out var strides);
                w.Write(strides.Length); foreach (uint stride in strides) w.Write(stride);
                w.Write(elements.Length);
                foreach (var e in elements)
                { w.Write(e.SemanticName); w.Write(e.SemanticIndex); w.Write((int)e.Format); w.Write(e.OffsetBytes); w.Write(e.Slot); w.Write(e.InstanceStepRate); }
            }
            var blends = vulkan ? VulkanGraphicsPipelineSpec.ResolveBlends(g.BlendAttachments.ToArray(), g.Colour.Length)
                : g.BlendAttachments.ToArray();
            w.Write(blends.Length);
            for (int i = 0; i < blends.Length; i++)
            {
                var b = blends[i];
                w.Write(b.BlendEnabled); w.Write((int)b.SourceColorFactor); w.Write((int)b.DestinationColorFactor);
                w.Write((int)b.ColorFunction); w.Write((int)b.SourceAlphaFactor); w.Write((int)b.DestinationAlphaFactor); w.Write((int)b.AlphaFunction);
            }
            w.Write(g.BlendFactor.X); w.Write(g.BlendFactor.Y); w.Write(g.BlendFactor.Z); w.Write(g.BlendFactor.W);
            if (!vulkan) Depth(w, g.DepthStencil);
            var s = g.Rasterizer;
            w.Write((int)s.CullMode); w.Write((int)s.FillMode); w.Write((int)s.FrontFace); w.Write(s.DepthClipEnabled); w.Write(s.ScissorTestEnabled);
            if (vulkan) Depth(w, g.DepthStencil);
            w.Write(g.Colour.Length); foreach (var c in g.Colour) w.Write((int)c);
            w.Write(g.Depth.HasValue); if (g.Depth is { } d) w.Write((int)d); w.Write(g.SampleCount);
        });
    static void Depth(BinaryWriter w, GpuDepthStencilState d)
    { w.Write(d.DepthTestEnabled); w.Write(d.DepthWriteEnabled); w.Write((int)d.Comparison); }
    internal static string Hash(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, true)) write(w);
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}

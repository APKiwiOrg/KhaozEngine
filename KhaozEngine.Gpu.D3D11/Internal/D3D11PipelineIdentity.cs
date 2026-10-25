using System;
using System.IO;
using System.Text;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Length-delimited content hashes. Native pointers and mutable caller arrays never enter a report.</summary>
internal static class D3D11PipelineIdentity
{
    internal static string Layout(D3D11ResourceLayout[] layouts) => Digest(w =>
    {
        w.Write(layouts.Length);
        foreach (var layout in layouts)
        {
            w.Write(layout.Elements.Length);
            foreach (var e in layout.Elements)
            {
                w.Write(e.Name ?? ""); w.Write((int)e.Kind); w.Write((int)e.Stages); w.Write(e.Dynamic);
            }
        }
    });

    internal static D3D11PipelineDiagnostics Compute(D3D11ShaderEvidence? shader, D3D11ResourceLayout[] layouts,
        uint x, uint y, uint z)
    {
        string layout = Layout(layouts);
        string pipeline = Digest(w =>
        {
            w.Write("compute,main,no-specialization"); w.Write(shader?.Artifact ?? "");
            w.Write(layout); w.Write(x); w.Write(y); w.Write(z);
        });
        return new([shader], pipeline, layout, x, y, z);
    }

    internal static D3D11PipelineDiagnostics Graphics(D3D11ShaderEvidence? vertex, D3D11ShaderEvidence? pixel,
        D3D11ResourceLayout[] layouts, in GpuPipelineDescription description)
    {
        var d = description;
        string layout = Layout(layouts);
        string pipeline = Digest(w =>
        {
            w.Write("graphics,vertex=main,pixel=main,no-specialization,dynamic=viewport+scissor,stencil=disabled");
            w.Write(vertex?.Artifact ?? ""); w.Write(pixel?.Artifact ?? ""); w.Write(layout);
            w.Write((int)d.Topology);
            var elements = D3D11InputLayoutPlan.Build(d.VertexLayouts, out uint[] strides);
            w.Write(strides.Length); foreach (uint stride in strides) w.Write(stride);
            w.Write(elements.Length);
            foreach (var e in elements)
            {
                w.Write(e.SemanticName); w.Write(e.SemanticIndex); w.Write((int)e.Format);
                w.Write(e.OffsetBytes); w.Write(e.Slot); w.Write(e.InstanceStepRate);
            }
            var blends = d.BlendAttachments ?? [];
            w.Write(blends.Length);
            foreach (var b in blends)
            {
                w.Write(b.BlendEnabled); w.Write((int)b.SourceColorFactor); w.Write((int)b.DestinationColorFactor);
                w.Write((int)b.ColorFunction); w.Write((int)b.SourceAlphaFactor);
                w.Write((int)b.DestinationAlphaFactor); w.Write((int)b.AlphaFunction);
            }
            w.Write(d.BlendFactor.X); w.Write(d.BlendFactor.Y); w.Write(d.BlendFactor.Z); w.Write(d.BlendFactor.W);
            w.Write(d.DepthStencil.DepthTestEnabled); w.Write(d.DepthStencil.DepthWriteEnabled); w.Write((int)d.DepthStencil.Comparison);
            w.Write((int)d.Rasterizer.CullMode); w.Write((int)d.Rasterizer.FillMode); w.Write((int)d.Rasterizer.FrontFace);
            w.Write(d.Rasterizer.DepthClipEnabled); w.Write(d.Rasterizer.ScissorTestEnabled);
            var colors = d.Outputs.Colour ?? [];
            w.Write(colors.Length); foreach (var color in colors) w.Write((int)color);
            w.Write(d.Outputs.Depth.HasValue); if (d.Outputs.Depth is { } depth) w.Write((int)depth);
            w.Write(d.Outputs.SampleCount);
        });
        return new([vertex, pixel], pipeline, layout);
    }

    internal static string Digest(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) write(writer);
        return D3D11ShaderEvidence.Hash(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }
}

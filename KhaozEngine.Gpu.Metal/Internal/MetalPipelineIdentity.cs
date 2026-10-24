using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Hash the resolved Metal state, not native handles or mutable caller descriptions.</summary>
internal static class MetalPipelineIdentity
{
    internal static string Layout(MetalResourceLayout[] layouts) => MetalShaderKey.Digest(w =>
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

    internal static string Compute(MetalShaderEvidence? shader, string layout, uint x, uint y, uint z)
        => MetalShaderKey.Digest(w =>
        {
            w.Write("metal-compute,no-specialization,default-mutability");
            WriteShader(w, shader); w.Write(layout); w.Write(x); w.Write(y); w.Write(z);
        });

    internal static string Graphics(MetalGraphicsPipelinePlan plan, string layout) => MetalShaderKey.Digest(w =>
    {
        w.Write("metal-graphics,no-specialization,dynamic-viewport-scissor");
        foreach (var shader in plan.Shaders.Evidence) WriteShader(w, shader);
        w.Write(layout); w.Write(plan.Streams.Length);
        foreach (var s in plan.Streams)
        {
            w.Write(s.BufferIndex); w.Write(s.Stride); w.Write((ulong)s.StepFunction); w.Write(s.StepRate);
        }
        w.Write(plan.Attributes.Length);
        foreach (var a in plan.Attributes)
        {
            w.Write(a.AttributeIndex); w.Write(a.BufferIndex); w.Write(a.OffsetBytes); w.Write((int)a.Format);
        }
        var state = plan.State;
        w.Write((ulong)state.CullMode); w.Write((ulong)state.FrontFace); w.Write((ulong)state.FillMode);
        w.Write((ulong)state.DepthClipMode); w.Write((ulong)state.PrimitiveType);
        w.Write(state.BlendColour.X); w.Write(state.BlendColour.Y); w.Write(state.BlendColour.Z); w.Write(state.BlendColour.W);
        w.Write(state.ScissorTestEnabled); w.Write(state.StencilReference);
        w.Write((ulong)state.DepthComparison); w.Write(state.DepthWriteEnabled);
        w.Write(plan.ColourAttachments.Length);
        foreach (var c in plan.ColourAttachments)
        {
            w.Write((ulong)c.Format); w.Write(c.BlendingEnabled); w.Write((ulong)c.WriteMask);
            w.Write((ulong)c.AlphaOperation); w.Write((ulong)c.SourceAlpha); w.Write((ulong)c.DestinationAlpha);
            w.Write((ulong)c.ColourOperation); w.Write((ulong)c.SourceColour); w.Write((ulong)c.DestinationColour);
        }
        w.Write(plan.DepthFormat.HasValue); if (plan.DepthFormat is { } d) w.Write((ulong)d);
        w.Write(plan.StencilFormat.HasValue); if (plan.StencilFormat is { } stencil) w.Write((ulong)stencil);
        w.Write(plan.SampleCount);
    });

    static void WriteShader(System.IO.BinaryWriter writer, MetalShaderEvidence? shader)
    {
        writer.Write(shader is not null);
        if (shader is null) return;
        writer.Write((int)shader.Stage); writer.Write(shader.EntryPoint); writer.Write(shader.SourceSha256);
        writer.Write(MetalShaderKey.HashUtf8(shader.Msl)); writer.Write(shader.Configuration);
    }
}

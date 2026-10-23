using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// One encoding also supplies collision value comparison. IDs and hashes are references, not recipe values.
internal static class RendererPerfConstructionEncoding
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string Digest(RendererPerfPipelineRecipe recipe, ImmutableArray<RendererPerfShaderRecipe> shaders,
        ImmutableArray<RendererPerfLayoutRecipe> layouts) => Convert.ToHexStringLower(SHA256.HashData(Bytes(recipe, shaders, layouts)));

    internal static byte[] Bytes(RendererPerfPipelineRecipe recipe, ImmutableArray<RendererPerfShaderRecipe> shaders,
        ImmutableArray<RendererPerfLayoutRecipe> layouts)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Utf8, true);
        Text(w, "khaozengine.renderer-construction-recipe/1");
        EnumValue(w, recipe.Kind);
        var shader = shaders[recipe.ShaderId];
        w.Write(checked((uint)shader.Stages.Length));
        foreach (var stage in shader.Stages)
        {
            Text(w, stage.Stage); Text(w, stage.EntryPoint); Text(w, stage.Source);
            w.Write(stage.LocalX); w.Write(stage.LocalY); w.Write(stage.LocalZ);
        }
        w.Write(checked((uint)recipe.LayoutIds.Length));
        foreach (int id in recipe.LayoutIds)
        {
            var layout = layouts[id]; w.Write(checked((uint)layout.Elements.Length));
            foreach (var e in layout.Elements)
            {
                Text(w, e.Name); EnumValue(w, e.Kind); Flags(w, e.Stages); w.Write(e.Dynamic);
            }
        }
        w.Write(recipe.Graphics is not null);
        if (recipe.Graphics is { } g) Graphics(w, g);
        DigestValue(w, recipe.CodeGenerationSha256);
        return stream.ToArray();
    }

    internal static void Graphics(BinaryWriter w, RendererPerfGraphicsRecipe g)
    {
        Float(w, g.BlendFactor.X); Float(w, g.BlendFactor.Y); Float(w, g.BlendFactor.Z); Float(w, g.BlendFactor.W);
        w.Write(checked((uint)g.BlendAttachments.Length));
        foreach (var b in g.BlendAttachments)
        {
            w.Write(b.BlendEnabled); EnumValue(w, b.SourceColorFactor); EnumValue(w, b.DestinationColorFactor);
            EnumValue(w, b.ColorFunction); EnumValue(w, b.SourceAlphaFactor); EnumValue(w, b.DestinationAlphaFactor); EnumValue(w, b.AlphaFunction);
        }
        w.Write(g.DepthStencil.DepthTestEnabled); w.Write(g.DepthStencil.DepthWriteEnabled); EnumValue(w, g.DepthStencil.Comparison);
        EnumValue(w, g.Rasterizer.CullMode); EnumValue(w, g.Rasterizer.FillMode); EnumValue(w, g.Rasterizer.FrontFace);
        w.Write(g.Rasterizer.DepthClipEnabled); w.Write(g.Rasterizer.ScissorTestEnabled); EnumValue(w, g.Topology);
        w.Write(checked((uint)g.VertexLayouts.Length));
        foreach (var v in g.VertexLayouts)
        {
            w.Write(v.Stride); w.Write(v.InstanceStepRate); w.Write(checked((uint)v.Elements.Length));
            foreach (var e in v.Elements) { Text(w, e.Name); EnumValue(w, e.Format); }
        }
        w.Write(g.Depth.HasValue); if (g.Depth is { } depth) EnumValue(w, depth);
        w.Write(checked((uint)g.Colour.Length)); foreach (var colour in g.Colour) EnumValue(w, colour);
        if (g.SampleCount < 1) throw new ArgumentException("Invalid sample count.");
        w.Write(g.SampleCount);
    }

    internal static void Text(BinaryWriter w, string value)
    {
        byte[] bytes = Utf8.GetBytes(value); w.Write(checked((uint)bytes.Length)); w.Write(bytes);
    }
    internal static void DigestValue(BinaryWriter w, GpuDiagnosticDigest d)
    {
        w.Write(d.Sha256 is not null); if (d.Sha256 is { } hash) Text(w, hash);
        Text(w, d.Source); EnumValue(w, d.Status); w.Write(d.Reason is not null); if (d.Reason is { } reason) Text(w, reason);
    }
    static void EnumValue<T>(BinaryWriter w, T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentException("Unknown construction enum.");
        w.Write(Convert.ToInt32(value));
    }
    static void Flags(BinaryWriter w, GpuShaderStages value)
    {
        if ((value & ~(GpuShaderStages.Vertex | GpuShaderStages.Fragment | GpuShaderStages.Compute)) != 0)
            throw new ArgumentException("Unknown shader stages.");
        w.Write((int)value);
    }
    static void Float(BinaryWriter w, float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentException("Nonfinite construction state.");
        w.Write(value);
    }
}

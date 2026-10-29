using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Logical lengths count declared-width encoding, not managed heap or backend allocation size.
internal sealed class RendererPerfConstructionAdmission
{
    internal const int PipelinesPerOperation = 256, FactoriesPerOperation = 1024;
    internal const int UniqueRecipes = 1024, CauseReserveBytes = 128;
    internal const long SourceBytes = 33554432, DescriptorBytes = 16777216, EvidenceBytes = 16777216, TotalBytes = 67108864;
    long _source, _descriptor, _evidence;

    internal void Reserve(long source, long descriptor, long evidence)
    {
        long s = checked(_source + source), d = checked(_descriptor + descriptor), e = checked(_evidence + evidence);
        if (s > SourceBytes) throw new RendererPerfConstructionRefusalException("Construction source byte capacity exceeded.");
        if (d > DescriptorBytes) throw new RendererPerfConstructionRefusalException("Construction descriptor byte capacity exceeded.");
        if (e > EvidenceBytes) throw new RendererPerfConstructionRefusalException("Construction event/occurrence/witness byte capacity exceeded.");
        if (checked(s + d + e) > TotalBytes) throw new RendererPerfConstructionRefusalException("Construction total logical byte capacity exceeded.");
        _source = s; _descriptor = d; _evidence = e;
    }

    internal static long Text(string value, int maximum = 1024)
    {
        int bytes = RendererPerfConstructionEncoding.Utf8.GetByteCount(value);
        if (bytes > maximum) throw new RendererPerfConstructionRefusalException("Construction text UTF-8 capacity exceeded.");
        return checked(4L + bytes);
    }
    internal static void Count(int value, int maximum, string name)
    {
        if (value > maximum) throw new RendererPerfConstructionRefusalException("Construction " + name + " capacity exceeded.");
    }
    internal static long Layout(in GpuResourceLayoutDescription d)
    {
        Count(d.Elements.Length, 128, "layout elements"); long bytes = 8;
        foreach (var e in d.Elements)
        {
            if (!Enum.IsDefined(e.Kind) || (e.Stages & ~(GpuShaderStages.Vertex | GpuShaderStages.Fragment | GpuShaderStages.Compute)) != 0)
                throw new RendererPerfConstructionRefusalException("Unknown layout enum.");
            bytes = checked(bytes + Text(e.Name) + 9);
        }
        return bytes;
    }
    internal static long Graphics(in GpuPipelineDescription d)
    {
        Count(d.ResourceLayouts.Length, 32, "layout sets"); Count(d.VertexLayouts.Count, 32, "vertex streams");
        Count(d.BlendAttachments.Length, 8, "blend attachments"); Count(d.Outputs.Colour.Length, 8, "color attachments");
        if (!float.IsFinite(d.BlendFactor.X) || !float.IsFinite(d.BlendFactor.Y)
            || !float.IsFinite(d.BlendFactor.Z) || !float.IsFinite(d.BlendFactor.W) || d.Outputs.SampleCount < 1
            || !Enum.IsDefined(d.Topology) || !Enum.IsDefined(d.DepthStencil.Comparison)
            || !Enum.IsDefined(d.Rasterizer.CullMode) || !Enum.IsDefined(d.Rasterizer.FillMode) || !Enum.IsDefined(d.Rasterizer.FrontFace))
            throw new RendererPerfConstructionRefusalException("Invalid graphics construction state.");
        long bytes = checked(16 + 4 + d.BlendAttachments.Length * 25L + 6 + 14 + 4 + 4 + 1 + (d.Outputs.Depth.HasValue ? 4 : 0)
            + 4 + d.Outputs.Colour.Length * 4L + 4 + 4 + d.ResourceLayouts.Length * 4L);
        foreach (var b in d.BlendAttachments)
            if (!Enum.IsDefined(b.SourceColorFactor) || !Enum.IsDefined(b.DestinationColorFactor) || !Enum.IsDefined(b.ColorFunction)
                || !Enum.IsDefined(b.SourceAlphaFactor) || !Enum.IsDefined(b.DestinationAlphaFactor) || !Enum.IsDefined(b.AlphaFunction))
                throw new RendererPerfConstructionRefusalException("Unknown blend enum.");
        foreach (var c in d.Outputs.Colour) if (!Enum.IsDefined(c)) throw new RendererPerfConstructionRefusalException("Unknown output format.");
        if (d.Outputs.Depth is { } depth && !Enum.IsDefined(depth)) throw new RendererPerfConstructionRefusalException("Unknown depth format.");
        foreach (var v in d.VertexLayouts)
        {
            Count(v.Elements.Length, 64, "vertex elements"); bytes = checked(bytes + 12);
            foreach (var e in v.Elements)
            {
                if (!Enum.IsDefined(e.Format)) throw new RendererPerfConstructionRefusalException("Unknown vertex format.");
                bytes = checked(bytes + Text(e.Name) + 4);
            }
        }
        return bytes;
    }
    internal static long Digest(GpuDiagnosticDigest d) => checked(1 + (d.Sha256 is null ? 0 : Text(d.Sha256, 64))
        + Text(d.Source, 16384) + 4 + 1 + (d.Reason is null ? 0 : Text(d.Reason, 16384)));
    internal static long Witness(GpuPipelineConstructionIdentity w)
    {
        Count(w.Stages.Length, 2, "witness stages");
        long bytes = checked(Digest(w.PipelineSha256) + Digest(w.SourceSha256) + Digest(w.ShaderArtifactSha256)
            + Digest(w.LayoutSha256) + Digest(w.DiagnosticConfigurationSha256) + Digest(w.CodeGenerationSha256)
            + Text(w.CompilerFlags, 16384) + 12 + 4);
        foreach (var s in w.Stages)
            bytes = checked(bytes + Text(s.Stage) + Text(s.EntryPoint) + Digest(s.SourceSha256)
                + 1 + (s.EmittedSourceSha256 is null ? 0 : Digest(s.EmittedSourceSha256)) + Digest(s.ShaderArtifactSha256));
        return bytes;
    }
}

// Distinguishes bounded harness refusals from an arbitrary backend getter exception.
internal sealed class RendererPerfConstructionRefusalException(string reason) : ArgumentException(reason);

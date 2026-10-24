using System;
using System.IO;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal;

internal sealed record VulkanShaderIdentity(string Source, string Artifact, string CompilerFlags,
    ImmutableArray<GpuPipelineStageIdentity> Stages, GpuDiagnosticDigest CodeGeneration)
{
    static GpuPipelineStageIdentity Stage(string stage, string source, ReadOnlySpan<byte> artifact)
        => new(stage, "main", GpuDiagnosticIdentityEncoding.Text(source, "glsl.utf8/1"), null,
            GpuDiagnosticIdentityEncoding.Bytes(artifact, "spirv.bytes/1"));
    static GpuDiagnosticDigest Options() => GpuDiagnosticIdentityEncoding.CodeGeneration(GpuBackendKind.VulkanNative,
        [new("frontend", SpirvFrontEndPin.Identity), new("toolchain", SpirvToolchainVersion.Identity)]);
    internal static VulkanShaderIdentity Compute(string source, ReadOnlySpan<byte> artifact, string label)
    {
        ImmutableArray<GpuPipelineStageIdentity> stages = [Stage("compute", source, artifact)];
        return Create(stages, label);
    }
    internal static VulkanShaderIdentity Graphics(string vertex, string fragment, byte[] vertexArtifact,
        byte[] fragmentArtifact, string label) => Create([Stage("vertex", vertex, vertexArtifact),
            Stage("fragment", fragment, fragmentArtifact)], label);
    static VulkanShaderIdentity Create(ImmutableArray<GpuPipelineStageIdentity> stages, string label)
    {
        int bytes = checked(GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(SpirvFrontEndPin.Identity) + 7
            + GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(label));
        if (bytes > 16384)
            throw new GpuDiagnosticFailureException([new("vulkan.shader-evidence",
                new InvalidOperationException("CompilerFlags exceeds 16384 UTF-8 bytes before retention."))]);
        return new(GpuDiagnosticIdentityEncoding.Stages("source", stages).Sha256 ?? "",
            GpuDiagnosticIdentityEncoding.Stages("artifact", stages).Sha256 ?? "", SpirvFrontEnd.OptionsIdentity(label), stages, Options());
    }
}

/// <summary>Content identity. Never hashes native handles, which vary between equivalent device lifetimes.</summary>
internal sealed record VulkanPipelineIdentity(string PipelineSha256, string SourceSha256, string ShaderArtifactSha256,
    string LayoutSha256, string CompilerFlags, uint LocalSizeX = 0, uint LocalSizeY = 0, uint LocalSizeZ = 0)
{
    internal ImmutableArray<GpuPipelineStageIdentity> Stages { get; init; } = [];
    internal GpuDiagnosticDigest CodeGeneration { get; init; } = new(null, "khaozengine.pipeline-code-generation/1",
        GpuDiagnosticStatus.Invalid, "Code-generation inputs were not retained.");

    internal static VulkanPipelineIdentity Compute(VulkanComputeShader shader, VulkanResourceLayout[] layouts)
    {
        string layout = Layout(layouts);
        string pipeline = Digest(writer =>
        {
            writer.Write("compute,main,no-specialization");
            writer.Write(shader.DiagnosticIdentity.Artifact);
            writer.Write(layout);
            writer.Write(shader.ThreadGroupSizeX);
            writer.Write(shader.ThreadGroupSizeY);
            writer.Write(shader.ThreadGroupSizeZ);
        });
        return new(pipeline, shader.DiagnosticIdentity.Source, shader.DiagnosticIdentity.Artifact, layout,
            shader.DiagnosticIdentity.CompilerFlags, shader.ThreadGroupSizeX, shader.ThreadGroupSizeY, shader.ThreadGroupSizeZ)
        { Stages = shader.DiagnosticIdentity.Stages, CodeGeneration = shader.DiagnosticIdentity.CodeGeneration };
    }

    internal static VulkanPipelineIdentity Graphics(VulkanShaderSet shaders, VulkanResourceLayout[] layouts,
        VulkanGraphicsPipelineSpec spec)
    {
        string layout = Layout(layouts);
        string pipeline = Digest(writer =>
        {
            writer.Write("graphics,vertex=main,fragment=main,no-specialization,dynamic=viewport+scissor");
            writer.Write(shaders.DiagnosticIdentity.Artifact);
            writer.Write(layout);
            writer.Write((int)spec.Topology);
            writer.Write(spec.VertexBindings.Length);
            foreach (VulkanVertexBinding binding in spec.VertexBindings)
            {
                writer.Write(binding.Binding); writer.Write(binding.Stride); writer.Write(binding.PerInstance);
            }
            writer.Write(spec.VertexAttributes.Length);
            foreach (VulkanVertexAttribute attribute in spec.VertexAttributes)
            {
                writer.Write(attribute.Location); writer.Write(attribute.Binding);
                writer.Write((int)attribute.Format); writer.Write(attribute.Offset);
            }
            writer.Write(spec.BlendAttachments.Length);
            foreach (GpuBlendAttachment blend in spec.BlendAttachments)
            {
                writer.Write(blend.BlendEnabled);
                writer.Write((int)blend.SourceColorFactor); writer.Write((int)blend.DestinationColorFactor);
                writer.Write((int)blend.ColorFunction); writer.Write((int)blend.SourceAlphaFactor);
                writer.Write((int)blend.DestinationAlphaFactor); writer.Write((int)blend.AlphaFunction);
            }
            writer.Write(spec.BlendFactor.X); writer.Write(spec.BlendFactor.Y);
            writer.Write(spec.BlendFactor.Z); writer.Write(spec.BlendFactor.W);
            writer.Write((int)spec.Rasterizer.CullMode); writer.Write((int)spec.Rasterizer.FillMode);
            writer.Write((int)spec.Rasterizer.FrontFace); writer.Write(spec.Rasterizer.DepthClipEnabled);
            writer.Write(spec.Rasterizer.ScissorTestEnabled);
            writer.Write(spec.DepthStencil.DepthTestEnabled); writer.Write(spec.DepthStencil.DepthWriteEnabled);
            writer.Write((int)spec.DepthStencil.Comparison);
            writer.Write(spec.ColourFormats.Length);
            foreach (GpuPixelFormat format in spec.ColourFormats) writer.Write((int)format);
            writer.Write(spec.DepthFormat.HasValue);
            if (spec.DepthFormat.HasValue) writer.Write((int)spec.DepthFormat.Value);
            writer.Write(spec.SampleCount);
        });
        return new(pipeline, shaders.DiagnosticIdentity.Source, shaders.DiagnosticIdentity.Artifact, layout,
            shaders.DiagnosticIdentity.CompilerFlags)
        { Stages = shaders.DiagnosticIdentity.Stages, CodeGeneration = shaders.DiagnosticIdentity.CodeGeneration };
    }

    static string Layout(VulkanResourceLayout[] layouts) => Digest(writer =>
    {
        writer.Write(layouts.Length);
        foreach (VulkanResourceLayout layout in layouts)
        {
            writer.Write(layout.Elements.Length);
            foreach (GpuResourceLayoutElement element in layout.Elements)
            {
                writer.Write(element.Name ?? ""); writer.Write((int)element.Kind);
                writer.Write((int)element.Stages); writer.Write(element.Dynamic);
            }
        }
    });

    internal static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string HashParts(byte[] first, byte[] second) => Digest(writer =>
    {
        writer.Write(first.Length); writer.Write(first);
        writer.Write(second.Length); writer.Write(second);
    });

    static string Digest(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) write(writer);
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}

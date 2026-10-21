using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal;

internal sealed record VulkanShaderIdentity(string Source, string Artifact, string CompilerFlags)
{
    internal static VulkanShaderIdentity Compute(string source, ReadOnlySpan<byte> artifact, string label)
        => new(VulkanPipelineIdentity.HashText(source), Convert.ToHexStringLower(SHA256.HashData(artifact)),
            SpirvFrontEnd.OptionsIdentity(label));

    internal static VulkanShaderIdentity Graphics(string vertex, string fragment, byte[] vertexArtifact,
        byte[] fragmentArtifact, string label)
        => new(VulkanPipelineIdentity.HashParts(Encoding.UTF8.GetBytes(vertex), Encoding.UTF8.GetBytes(fragment)),
            VulkanPipelineIdentity.HashParts(vertexArtifact, fragmentArtifact), SpirvFrontEnd.OptionsIdentity(label));
}

/// <summary>Content identity. Never hashes native handles, which vary between equivalent device lifetimes.</summary>
internal sealed record VulkanPipelineIdentity(string PipelineSha256, string SourceSha256, string ShaderArtifactSha256,
    string LayoutSha256, string CompilerFlags, uint LocalSizeX = 0, uint LocalSizeY = 0, uint LocalSizeZ = 0)
{
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
            shader.DiagnosticIdentity.CompilerFlags, shader.ThreadGroupSizeX, shader.ThreadGroupSizeY, shader.ThreadGroupSizeZ);
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
            shaders.DiagnosticIdentity.CompilerFlags);
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

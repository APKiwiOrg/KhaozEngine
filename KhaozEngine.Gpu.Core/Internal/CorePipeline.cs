using System;
using KhaozEngine.Gpu.Core.Interop;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CorePipeline(CoreGpuDevice device, khaoz_gpu_pipeline* handle, CoreResourceLayout[] layouts)
    : CoreResource(device, handle), IGpuPipeline
{
    internal khaoz_gpu_pipeline* Handle => (khaoz_gpu_pipeline*)NativeHandle;

    internal bool Accepts(uint slot, CoreResourceSet set)
        => slot < (uint)layouts.Length && layouts[slot].Bindings.SequenceEqual(set.Layout.Bindings);

    internal static CorePipeline Create(CoreGpuDevice device, in GpuPipelineDescription description)
    {
        const string operation = nameof(IGpuResourceFactory.CreateGraphicsPipeline);
        const string context = "a native Vulkan graphics pipeline";
        lock (device.Gate)
        {
            var shaders = RequireOwned<CoreShaderSet>(device, description.ShaderSet, "shaders", "shader set", context);
            IGpuResourceLayout[] declared = description.ResourceLayouts ?? [];
            var layouts = new CoreResourceLayout[declared.Length];
            var layoutHandles = new nint[declared.Length];
            int uniformCount = 0;
            for (int i = 0; i < layouts.Length; i++)
            {
                var layout = RequireOwned<CoreResourceLayout>(device, declared[i], "layout", "resource layout", context);
                layouts[i] = layout;
                layoutHandles[i] = (nint)layout.Handle;
                uniformCount = checked(uniformCount + layout.DynamicUniformCount);
            }
            VulkanDescriptorLimits.RequirePipelineWithinLimit(uniformCount, device.DynamicUniformBufferLimit, layouts.Length);

            var vertexBindings = VulkanVertexInput.Build(description.VertexLayouts, out var attributes);
            var vertexLayouts = new khaoz_gpu_vertex_layout[vertexBindings.Length];
            var vertexFormats = new uint[attributes.Length];
            var outputFormats = Array.ConvertAll(description.Outputs.Colour ?? [], format => (uint)format);
            var blends = Array.ConvertAll(VulkanGraphicsPipelineSpec.ResolveBlends(description.BlendAttachments,
                outputFormats.Length), ConvertBlend);
            uint depthFormat = (uint)description.Outputs.Depth.GetValueOrDefault();

            fixed (nint* nativeLayouts = layoutHandles)
            fixed (khaoz_gpu_vertex_layout* nativeVertices = vertexLayouts)
            fixed (uint* formats = vertexFormats, colors = outputFormats)
            fixed (khaoz_gpu_blend_attachment* nativeBlends = blends)
            {
                int next = 0;
                for (int slot = 0; slot < vertexLayouts.Length; slot++)
                {
                    int start = next;
                    while (next < attributes.Length && attributes[next].Binding == (uint)slot)
                    {
                        vertexFormats[next] = (uint)attributes[next].Format;
                        next++;
                    }

                    vertexLayouts[slot] = new khaoz_gpu_vertex_layout
                    {
                        formats = start == next ? null : formats + start,
                        count = (uint)(next - start),
                        stride = vertexBindings[slot].Stride,
                        instance_step_rate = vertexBindings[slot].PerInstance ? 1u : 0,
                    };
                }

                var desc = ConvertState(description);
                desc.shaders = shaders.Handle;
                desc.layouts = (khaoz_gpu_resource_layout**)nativeLayouts;
                desc.layout_count = (uint)layouts.Length;
                desc.vertex_layouts = nativeVertices;
                desc.vertex_layout_count = (uint)vertexLayouts.Length;
                desc.color_formats = colors;
                desc.blends = nativeBlends;
                desc.color_count = (uint)outputFormats.Length;
                desc.depth_format = description.Outputs.Depth.HasValue ? &depthFormat : null;

                khaoz_gpu_pipeline* pipeline;
                NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_pipeline_create(device.Handle, &desc, &pipeline), operation);
                return new CorePipeline(device, pipeline, layouts);
            }
        }
    }

    private static khaoz_gpu_pipeline_desc ConvertState(in GpuPipelineDescription description)
    {
        var depth = description.DepthStencil;
        var raster = description.Rasterizer;
        var desc = new khaoz_gpu_pipeline_desc
        {
            depth_test = depth.DepthTestEnabled ? 1u : 0,
            depth_write = depth.DepthWriteEnabled ? 1u : 0,
            depth_compare = (uint)depth.Comparison,
            depth_clip = raster.DepthClipEnabled ? 1u : 0,
            cull = (uint)raster.CullMode,
            fill = (uint)raster.FillMode,
            front_face = (uint)raster.FrontFace,
            topology = (uint)description.Topology,
            sample_count = (uint)Math.Max(1, description.Outputs.SampleCount),
        };
        desc.blend_constant[0] = description.BlendFactor.X;
        desc.blend_constant[1] = description.BlendFactor.Y;
        desc.blend_constant[2] = description.BlendFactor.Z;
        desc.blend_constant[3] = description.BlendFactor.W;
        return desc;
    }

    private static khaoz_gpu_blend_attachment ConvertBlend(GpuBlendAttachment blend) => new()
    {
        enabled = blend.BlendEnabled ? 1u : 0,
        src_color = (uint)blend.SourceColorFactor,
        dst_color = (uint)blend.DestinationColorFactor,
        color_op = (uint)blend.ColorFunction,
        src_alpha = (uint)blend.SourceAlphaFactor,
        dst_alpha = (uint)blend.DestinationAlphaFactor,
        alpha_op = (uint)blend.AlphaFunction,
    };

    protected override khaoz_result Release(void* value)
        => KhaozNative.khaoz_gpu_pipeline_destroy((khaoz_gpu_pipeline*)value);
}

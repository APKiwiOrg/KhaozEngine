#include "pipeline.hpp"
#include "shaders.hpp"

#include <array>

using namespace khaoz::vulkan;

namespace {

constexpr std::array topologies{VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST, VK_PRIMITIVE_TOPOLOGY_TRIANGLE_STRIP,
                                VK_PRIMITIVE_TOPOLOGY_LINE_LIST, VK_PRIMITIVE_TOPOLOGY_LINE_STRIP,
                                VK_PRIMITIVE_TOPOLOGY_POINT_LIST};
constexpr std::array culls{VK_CULL_MODE_BACK_BIT, VK_CULL_MODE_FRONT_BIT, VK_CULL_MODE_NONE};
constexpr std::array faces{VK_FRONT_FACE_CLOCKWISE, VK_FRONT_FACE_COUNTER_CLOCKWISE};
constexpr std::array fills{VK_POLYGON_MODE_FILL, VK_POLYGON_MODE_LINE};
constexpr std::array vertex_formats{VK_FORMAT_R32_SFLOAT, VK_FORMAT_R32G32_SFLOAT, VK_FORMAT_R32G32B32_SFLOAT,
                                    VK_FORMAT_R32G32B32A32_SFLOAT};
constexpr std::array blend_factors{VK_BLEND_FACTOR_ZERO,           VK_BLEND_FACTOR_ONE,
                                   VK_BLEND_FACTOR_SRC_COLOR,      VK_BLEND_FACTOR_ONE_MINUS_SRC_COLOR,
                                   VK_BLEND_FACTOR_SRC_ALPHA,      VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA,
                                   VK_BLEND_FACTOR_DST_COLOR,      VK_BLEND_FACTOR_ONE_MINUS_DST_COLOR,
                                   VK_BLEND_FACTOR_DST_ALPHA,      VK_BLEND_FACTOR_ONE_MINUS_DST_ALPHA,
                                   VK_BLEND_FACTOR_CONSTANT_COLOR, VK_BLEND_FACTOR_ONE_MINUS_CONSTANT_COLOR};
constexpr std::array blend_ops{VK_BLEND_OP_ADD, VK_BLEND_OP_SUBTRACT, VK_BLEND_OP_REVERSE_SUBTRACT, VK_BLEND_OP_MIN,
                               VK_BLEND_OP_MAX};
constexpr auto color_mask = VkColorComponentFlags{VK_COLOR_COMPONENT_R_BIT} | VK_COLOR_COMPONENT_G_BIT |
                            VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;

struct vertex_input {
    std::vector<VkVertexInputBindingDescription> bindings;
    std::vector<VkVertexInputAttributeDescription> attributes;

    bool build(const khaoz_gpu_pipeline_desc& desc, const VkPhysicalDeviceLimits& limits) {
        for (uint32_t slot = 0; slot < desc.vertex_layout_count; ++slot) {
            const auto& vertex = desc.vertex_layouts[slot];
            if ((vertex.count != 0 && vertex.formats == nullptr) || vertex.instance_step_rate > 1 ||
                vertex.count > limits.maxVertexInputAttributes - attributes.size())
                return false;

            uint32_t offset = 0;
            for (uint32_t i = 0; i < vertex.count; ++i) {
                const auto format = vertex.formats[i];
                if (format >= vertex_formats.size() || offset > limits.maxVertexInputAttributeOffset)
                    return false;
                attributes.push_back(
                    {static_cast<uint32_t>(attributes.size()), slot, vertex_formats.at(format), offset});
                offset += (format + 1U) * sizeof(float);
            }
            const auto stride = vertex.stride != 0 ? vertex.stride : offset;
            if (stride > limits.maxVertexInputBindingStride)
                return false;
            bindings.push_back(
                {slot, stride,
                 vertex.instance_step_rate != 0 ? VK_VERTEX_INPUT_RATE_INSTANCE : VK_VERTEX_INPUT_RATE_VERTEX});
        }
        return true;
    }
};

bool build_blends(const khaoz_gpu_pipeline_desc& desc, std::vector<VkPipelineColorBlendAttachmentState>& blends,
                  bool independent) {
    for (uint32_t i = 0; i < desc.color_count; ++i) {
        const auto& b = desc.blends[i];
        if (b.src_color >= blend_factors.size() || b.dst_color >= blend_factors.size() ||
            b.src_alpha >= blend_factors.size() || b.dst_alpha >= blend_factors.size() ||
            b.color_op >= blend_ops.size() || b.alpha_op >= blend_ops.size())
            return false;
        const auto encode = [](const khaoz_gpu_blend_attachment& a) {
            return std::array{a.enabled, a.src_color, a.dst_color, a.color_op, a.src_alpha, a.dst_alpha, a.alpha_op};
        };
        if (!independent && i != 0 && encode(b) != encode(desc.blends[0]))
            return false;
        blends.push_back({b.enabled != 0, blend_factors.at(b.src_color), blend_factors.at(b.dst_color),
                          blend_ops.at(b.color_op), blend_factors.at(b.src_alpha), blend_factors.at(b.dst_alpha),
                          blend_ops.at(b.alpha_op), color_mask});
    }
    return true;
}

VkPipelineShaderStageCreateInfo shader_stage(VkShaderStageFlagBits stage, VkShaderModule module) {
    return {.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
            .pNext = nullptr,
            .flags = 0,
            .stage = stage,
            .module = module,
            .pName = "main",
            .pSpecializationInfo = nullptr};
}

} // namespace

khaoz_result khaoz::vulkan::create_graphics_pipeline(khaoz_gpu_pipeline& pipeline,
                                                     const khaoz_gpu_pipeline_desc& desc) {
    const auto& device = pipeline.device;
    const auto& features = device.adapter.features;
    if (desc.topology >= topologies.size() || desc.cull >= culls.size() || desc.front_face >= faces.size() ||
        desc.fill >= fills.size() || desc.depth_compare > VK_COMPARE_OP_ALWAYS)
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The Vulkan pipeline state has an unknown enum value");
    if ((desc.depth_clip == 0 && features.depthClamp == VK_FALSE) ||
        (desc.fill != 0 && features.fillModeNonSolid == VK_FALSE))
        return error(KHAOZ_ERROR_UNSUPPORTED, "The device cannot express the Vulkan pipeline rasterizer state");

    vertex_input vertex;
    std::vector<VkPipelineColorBlendAttachmentState> blends;
    if (!vertex.build(desc, device.adapter.properties.limits) ||
        !build_blends(desc, blends, features.independentBlend != VK_FALSE))
        return error(KHAOZ_ERROR_INVALID_ARGUMENT, "The Vulkan vertex layout or blend state is invalid");

    const std::array stages{shader_stage(VK_SHADER_STAGE_VERTEX_BIT, desc.shaders->vertex),
                            shader_stage(VK_SHADER_STAGE_FRAGMENT_BIT, desc.shaders->fragment)};
    auto input = info<VkPipelineVertexInputStateCreateInfo>(VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO);
    input.vertexBindingDescriptionCount = static_cast<uint32_t>(vertex.bindings.size());
    input.pVertexBindingDescriptions = vertex.bindings.data();
    input.vertexAttributeDescriptionCount = static_cast<uint32_t>(vertex.attributes.size());
    input.pVertexAttributeDescriptions = vertex.attributes.data();

    auto assembly =
        info<VkPipelineInputAssemblyStateCreateInfo>(VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO);
    assembly.topology = topologies.at(desc.topology);
    auto viewport = info<VkPipelineViewportStateCreateInfo>(VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO);
    viewport.viewportCount = 1;
    viewport.scissorCount = 1;

    auto raster =
        info<VkPipelineRasterizationStateCreateInfo>(VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO);
    raster.depthClampEnable = desc.depth_clip == 0;
    raster.polygonMode = fills.at(desc.fill);
    raster.cullMode = culls.at(desc.cull);
    raster.frontFace = faces.at(desc.front_face);
    raster.lineWidth = 1.0F;
    const VkPipelineMultisampleStateCreateInfo multisample{
        .sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO,
        .pNext = nullptr,
        .flags = 0,
        .rasterizationSamples = static_cast<VkSampleCountFlagBits>(desc.sample_count),
        .sampleShadingEnable = VK_FALSE,
        .minSampleShading = 0.0F,
        .pSampleMask = nullptr,
        .alphaToCoverageEnable = VK_FALSE,
        .alphaToOneEnable = VK_FALSE,
    };

    auto depth =
        info<VkPipelineDepthStencilStateCreateInfo>(VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO);
    depth.depthTestEnable = desc.depth_test != 0;
    depth.depthWriteEnable = desc.depth_write != 0;
    depth.depthCompareOp = static_cast<VkCompareOp>(desc.depth_compare);

    auto blend = info<VkPipelineColorBlendStateCreateInfo>(VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO);
    blend.attachmentCount = desc.color_count;
    blend.pAttachments = blends.data();
    std::copy_n(desc.blend_constant, 4, blend.blendConstants);
    constexpr std::array states{VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR};
    auto dynamics = info<VkPipelineDynamicStateCreateInfo>(VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO);
    dynamics.dynamicStateCount = static_cast<uint32_t>(states.size());
    dynamics.pDynamicStates = states.data();

    auto rendering = info<VkPipelineRenderingCreateInfo>(VK_STRUCTURE_TYPE_PIPELINE_RENDERING_CREATE_INFO);
    rendering.colorAttachmentCount = desc.color_count;
    rendering.pColorAttachmentFormats = pipeline.color_formats.data();
    rendering.depthAttachmentFormat = pipeline.depth_format;
    rendering.stencilAttachmentFormat = pipeline.stencil_format;

    auto create = info<VkGraphicsPipelineCreateInfo>(VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO);
    create.pNext = &rendering;
    create.stageCount = static_cast<uint32_t>(stages.size());
    create.pStages = stages.data();
    create.pVertexInputState = &input;
    create.pInputAssemblyState = &assembly;
    create.pViewportState = &viewport;
    create.pRasterizationState = &raster;
    create.pMultisampleState = &multisample;
    create.pDepthStencilState = &depth;
    create.pColorBlendState = &blend;
    create.pDynamicState = &dynamics;
    create.layout = pipeline.layout;
    pipeline.vertex_bindings = desc.vertex_layout_count;
    return check(create_handle(pipeline.handle,
                               [&](auto* pending) {
                                   return device.api.vkCreateGraphicsPipelines(device.handle, VK_NULL_HANDLE, 1,
                                                                               &create, nullptr, pending);
                               }),
                 "Could not create a Vulkan graphics pipeline");
}

using System.Collections.Immutable;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal enum RendererPerfFactoryKind { Shader, Layout, GraphicsPipeline, ComputePipeline }
internal sealed record RendererPerfStageSource(string Stage, string EntryPoint, string Source, uint LocalX, uint LocalY, uint LocalZ);
internal sealed record RendererPerfShaderRecipe(int Id, ImmutableArray<RendererPerfStageSource> Stages);
internal sealed record RendererPerfLayoutRecipe(int Id, ImmutableArray<GpuResourceLayoutElement> Elements);
internal sealed record RendererPerfVertexRecipe(uint Stride, uint InstanceStepRate, ImmutableArray<GpuVertexElement> Elements);
internal sealed record RendererPerfGraphicsRecipe(Vector4 BlendFactor, ImmutableArray<GpuBlendAttachment> BlendAttachments,
    GpuDepthStencilState DepthStencil, GpuRasterizerState Rasterizer, GpuPrimitiveTopology Topology,
    ImmutableArray<RendererPerfVertexRecipe> VertexLayouts, GpuPixelFormat? Depth, ImmutableArray<GpuPixelFormat> Colour, int SampleCount);
internal sealed record RendererPerfPipelineRecipe(int Id, RendererPerfPipelineKind Kind, int ShaderId,
    ImmutableArray<int> LayoutIds, RendererPerfGraphicsRecipe? Graphics, GpuDiagnosticDigest CodeGenerationSha256, string RecipeSha256);
internal sealed record RendererPerfFactoryEvent(int OperationOrdinal, int Ordinal, RendererPerfFactoryKind Kind,
    int InputId, int? OutputId, string? Failure);
internal sealed record RendererPerfPipelineOccurrence(int Id, int OperationOrdinal, int CreationOrdinal,
    RendererPerfConstructionStage Stage, int? FrameIndex, RendererPerfPipelineKind Kind, int RecipeId,
    GpuPipelineConstructionIdentity Production, long BindCount);
internal sealed record RendererPerfCompilerInventory(string Schema, ImmutableArray<string> Operations,
    ImmutableArray<RendererPerfFactoryEvent> FactoryEvents, ImmutableArray<RendererPerfShaderRecipe> Shaders,
    ImmutableArray<RendererPerfLayoutRecipe> Layouts, ImmutableArray<RendererPerfPipelineRecipe> Recipes,
    ImmutableArray<RendererPerfPipelineOccurrence> Occurrences, RendererPerfCompilerCounts Counts, bool Complete, string? Reason);

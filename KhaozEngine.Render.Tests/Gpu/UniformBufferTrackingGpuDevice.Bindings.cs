using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal sealed partial class UniformBufferTrackingGpuDevice
{
    internal sealed record ProgramBinding(string Vertex, string Fragment, IGpuResourceLayout[] Layouts, int VertexLayouts);
    internal sealed record SetBinding(IGpuResourceLayout Layout, IGpuBindableResource[] Resources);

    internal ProgramBinding? ProgramOf(IGpuPipeline pipeline) =>
        _factory.Programs.TryGetValue(pipeline, out var binding) ? binding : null;

    internal SetBinding ResourcesOf(IGpuResourceSet set) => _factory.Sets.GetValue(set,
        static _ => throw new System.InvalidOperationException("Resource set was not created through the tracking factory."));

    sealed partial class TrackingFactory
    {
        readonly ConditionalWeakTable<IGpuShaderSet, ProgramBinding> shaders = new();
        internal ConditionalWeakTable<IGpuPipeline, ProgramBinding> Programs { get; } = new();
        internal ConditionalWeakTable<IGpuResourceSet, SetBinding> Sets { get; } = new();

        void TrackSet(IGpuResourceSet set, in GpuResourceSetDescription description) => Sets.AddOrUpdate(set,
            new(description.Layout, description.Resources is { } resources ? (IGpuBindableResource[])resources.Clone() : []));

        IGpuShaderSet TrackShaders(IGpuShaderSet shader, string vertex, string fragment)
        {
            shaders.AddOrUpdate(shader, new(vertex, fragment, [], 0));
            return shader;
        }

        IGpuPipeline TrackPipeline(IGpuPipeline pipeline, in GpuPipelineDescription description)
        {
            if (description.ShaderSet is { } handle && shaders.TryGetValue(handle, out var shader))
                Programs.AddOrUpdate(pipeline, shader with
                {
                    Layouts = description.ResourceLayouts is { } layouts ? (IGpuResourceLayout[])layouts.Clone() : [],
                    VertexLayouts = description.VertexLayouts?.Count ?? 0,
                });
            return pipeline;
        }
    }
}

using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// What the fullscreen fragment passes share: the layout element shorthands their resource layouts are written with,
/// and the release of a resource a pass owns. Imported with <c>using static</c>, so a layout reads as a list of
/// <c>T("Name")</c>, <c>S("Name")</c> and <c>U("Name")</c>.
/// </summary>
internal static class FragmentPassResources
{
    /// <summary>A texture the fragment stage reads.</summary>
    internal static GpuResourceLayoutElement T(string n) => new(n, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);

    /// <summary>A sampler the fragment stage reads.</summary>
    internal static GpuResourceLayoutElement S(string n) => new(n, GpuResourceKind.Sampler, GpuShaderStages.Fragment);

    /// <summary>A uniform buffer the fragment stage reads.</summary>
    internal static GpuResourceLayoutElement U(string n) => new(n, GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment);

    /// <summary>Hand <paramref name="resource"/> to <paramref name="retired"/>, so it is disposed once the frames in
    /// flight that may read it complete, or dispose it at once when there is no queue, then clear the field.</summary>
    internal static void Free<T>(ref T? resource, GpuRetireQueue? retired) where T : class, IDisposable
    {
        if (retired is not null) retired.Retire(resource);
        else resource?.Dispose();
        resource = null;
    }
}

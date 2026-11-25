using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Owns layout-selected opaque output variants without changing the base or motion programs.</summary>
internal sealed class ModelVisibilityShaders(bool releaseOnFallback = true) : IDisposable
{
    readonly Dictionary<(string Vertex, string Fragment, int VisibilitySlot, int AmbientSlot, bool Preserve, bool Precise), IGpuShaderSet> _sets = new();

    internal bool TrySelect(IGpuResourceFactory factory, in GpuOutputDescription outputs,
        string vertex, string fragment, bool preserve, out IGpuShaderSet shaders, GpuShaderCompileOptions options = default)
    {
        bool motion = MotionMath.IsTemporal(outputs);
        int visibility = ShadowVisibilityTargets.HasVisibility(outputs) ? ShadowVisibilityTargets.Attachment(motion) : -1;
        int ambient = AmbientDiffuseTarget.IsAttached(outputs) ? outputs.Colour.Length - 1 : -1;
        if (visibility < 0 && ambient < 0)
        {
            // The opaque renderer changes outputs transactionally and handles retirement after publication.
            if (releaseOnFallback) Dispose();
            shaders = null!;
            return false;
        }
        var key = (vertex, fragment, visibility, ambient, preserve, options.PreciseMath);
        if (_sets.TryGetValue(key, out IGpuShaderSet? found))
        {
            shaders = found;
            return true;
        }
        if (visibility >= 0) fragment = ShadowVisibilityTargets.Fragment(fragment, motion, preserve);
        if (ambient >= 0) fragment = AmbientDiffuseTarget.Fragment(fragment, ambient);
        shaders = factory.CreateShadersFromSpirv(vertex, fragment, options);
        _sets.Add(key, shaders);
        return true;
    }

    public void Dispose() => Release(null);

    internal void EnsureCompilationReady()
    {
        foreach (var shaders in _sets.Values) shaders.EnsureCompilationReady();
    }

    internal void ReleaseAmbientVariants(GpuRetireQueue retired)
    {
        List<(string Vertex, string Fragment, int VisibilitySlot, int AmbientSlot, bool Preserve, bool Precise)>? remove = null;
        foreach (var (key, shaders) in _sets)
        {
            if (key.AmbientSlot < 0) continue;
            retired.Retire(shaders);
            (remove ??= []).Add(key);
        }
        if (remove is not null)
            foreach (var key in remove) _sets.Remove(key);
    }

    internal void Release(GpuRetireQueue? retired)
    {
        foreach (IGpuShaderSet shaders in _sets.Values) ResourceRetirement.Free(shaders, retired);
        _sets.Clear();
    }
}

using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Owns layout-selected visibility variants without changing the base or motion programs.</summary>
internal sealed class ModelVisibilityShaders : IDisposable
{
    readonly Dictionary<(string Vertex, string Fragment, bool Motion, bool Preserve), IGpuShaderSet> _sets = new();

    internal bool TrySelect(IGpuResourceFactory factory, in GpuOutputDescription outputs,
        string vertex, string fragment, bool preserve, out IGpuShaderSet shaders)
    {
        if (!ShadowVisibilityTargets.HasVisibility(outputs))
        {
            Dispose();
            shaders = null!;
            return false;
        }
        var key = (vertex, fragment, MotionMath.IsTemporal(outputs), preserve);
        if (_sets.TryGetValue(key, out IGpuShaderSet? found))
        {
            shaders = found;
            return true;
        }
        shaders = factory.CreateShadersFromSpirv(vertex,
            ShadowVisibilityTargets.Fragment(fragment, key.Item3, preserve));
        _sets.Add(key, shaders);
        return true;
    }

    public void Dispose() => Release(null);

    internal void Release(GpuRetireQueue? retired)
    {
        foreach (IGpuShaderSet shaders in _sets.Values) ResourceRetirement.Free(shaders, retired);
        _sets.Clear();
    }
}

using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Owns layout-selected visibility variants without changing the base or motion programs.</summary>
internal sealed class ModelVisibilityShaders : IDisposable
{
    readonly Dictionary<(string Vertex, string Fragment, bool Motion, bool Preserve), IGpuShaderSet> _sets = new();

    internal IGpuShaderSet Select(IGpuResourceFactory factory, in GpuOutputDescription outputs,
        string vertex, string fragment, IGpuShaderSet fallback, bool preserve = false)
    {
        if (!ShadowVisibilityTargets.HasVisibility(outputs))
        {
            Dispose();
            return fallback;
        }
        var key = (vertex, fragment, MotionMath.IsTemporal(outputs), preserve);
        if (_sets.TryGetValue(key, out IGpuShaderSet? found)) return found;
        IGpuShaderSet shaders = factory.CreateShadersFromSpirv(vertex,
            ShadowVisibilityTargets.Fragment(fragment, key.Item3, preserve));
        _sets.Add(key, shaders);
        return shaders;
    }

    public void Dispose()
    {
        foreach (IGpuShaderSet shaders in _sets.Values) shaders.Dispose();
        _sets.Clear();
    }
}

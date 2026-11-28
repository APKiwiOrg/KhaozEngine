using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu;

/// <summary>Checks a compiled program's bindings against the resource layouts a pipeline declares.</summary>
public static class GpuShaderBindingCheck
{
    /// <summary>Refuses a binding with no layout element of the same kind at its set and position.</summary>
    public static void RequireMatches(GpuShaderProgram program, IReadOnlyList<GpuResourceLayoutDescription> layouts)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(layouts);
        foreach (var binding in program.Bindings)
        {
            var elements = (uint)binding.Set < (uint)layouts.Count ? layouts[binding.Set].Elements : null;
            GpuResourceKind? declared = elements is not null && (uint)binding.Binding < (uint)elements.Length
                ? elements[binding.Binding].Kind : null;
            if (declared == binding.Kind)
                continue;
            throw new ShaderValidationException(ShaderValidationRefusal.BundleLayoutMismatch,
                $"Shader program '{program.Id}' reads {binding.Kind} '{binding.Name}' at set {binding.Set} binding {binding.Binding}, but the pipeline layout declares {declared?.ToString() ?? "nothing"} there.")
            {
                Label = program.Id,
                Set = binding.Set,
                Binding = binding.Binding,
            };
        }
    }
}

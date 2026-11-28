using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace KhaozEngine.Gpu;

/// <summary>A compiled vertex and fragment program with the bindings it reads.</summary>
public sealed class GpuShaderProgram
{
    internal GpuShaderProgram(string id, string variant, bool vertexPreciseMath,
        GpuShaderStageCode vertex, GpuShaderStageCode fragment, IEnumerable<GpuShaderBinding> bindings)
    {
        Id = id;
        Variant = variant;
        VertexPreciseMath = vertexPreciseMath;
        Vertex = vertex;
        Fragment = fragment;
        Bindings = bindings.OrderBy(b => b.Set).ThenBy(b => b.Binding).ToImmutableArray();
    }

    /// <summary>The program id.</summary>
    public string Id { get; }

    /// <summary>The variant name, empty for the default variant.</summary>
    public string Variant { get; }

    /// <summary>Whether the vertex stage compiles with precise arithmetic.</summary>
    public bool VertexPreciseMath { get; }

    /// <summary>The vertex stage.</summary>
    public GpuShaderStageCode Vertex { get; }

    /// <summary>The fragment stage.</summary>
    public GpuShaderStageCode Fragment { get; }

    /// <summary>The bindings, sorted by set then binding.</summary>
    public IReadOnlyList<GpuShaderBinding> Bindings { get; }
}

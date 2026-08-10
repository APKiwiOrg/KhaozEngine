namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// What <see cref="SpirvCrossCompile"/> hands back for a vertex and fragment PAIR: the emitted source for each
    /// stage, plus the reflection the backend needs to bind against it.
    /// <para>
    /// ONE MIRROR FOR BOTH BACK-END TARGETS, which is why the members are named for the STAGE rather than for the
    /// language (decision M-S1, section 12.1 of the Metal design). The HLSL pair and the MSL pair return the same
    /// shape, so a second nearly identical record does not exist and cannot drift from this one. What language a
    /// given instance carries is decided by the member that produced it, and the emitting member says so in its
    /// own name.
    /// </para>
    /// <para>
    /// Deliberately Veldrid-free, and this file names no Veldrid type at all, which is the visible half of
    /// decision P2. <c>KhaozEngine.Gpu.D3D11</c> and <c>KhaozEngine.Gpu.Metal</c> read these types across
    /// <c>InternalsVisibleTo</c>, and a Veldrid type anywhere in this shape would put a Veldrid assembly reference
    /// in a backend's IL through an internal API that no public-surface scan checks. The engine mirrors
    /// (<see cref="GpuVertexElement"/>, <see cref="GpuResourceLayoutDescription"/>) already exist for exactly
    /// this purpose, so nothing new is invented here.
    /// </para>
    /// </summary>
    /// <param name="VertexSource">The emitted vertex-stage source, in the emitting member's target language.</param>
    /// <param name="FragmentSource">The emitted fragment-stage source, in the same language.</param>
    /// <param name="Reflection">The reflected vertex inputs and resource layouts.</param>
    internal readonly record struct CrossCompiledPair(
        string VertexSource,
        string FragmentSource,
        ShaderReflection Reflection);

    /// <summary>The compute sibling of <see cref="CrossCompiledPair"/>: one emitted stage plus its
    /// reflection.</summary>
    /// <param name="ComputeSource">The emitted compute-stage source, in the emitting member's target
    /// language.</param>
    /// <param name="Reflection">The reflected resource layouts. <see cref="ShaderReflection.VertexElements"/> is
    /// empty for a compute module.</param>
    internal readonly record struct CrossCompiledCompute(
        string ComputeSource,
        ShaderReflection Reflection);

    /// <summary>
    /// The reflection a cross-compiled module carries, in engine types: the vertex input signature in location
    /// order, and the resource layouts in SET order.
    /// <para>
    /// Set order is load-bearing rather than incidental. The Direct3D 11 register scheme flattens layouts in
    /// PIPELINE-ARRAY order, per kind, and never by the GLSL <c>set=</c> number, so anything that reorders this
    /// array renumbers every register in the program (section 8.1 of the design, decision S2).
    /// </para>
    /// </summary>
    /// <param name="VertexElements">The reflected vertex inputs, in location order. Empty for compute.</param>
    /// <param name="ResourceLayouts">The reflected resource layouts, in set order.</param>
    internal sealed record ShaderReflection(
        GpuVertexElement[] VertexElements,
        GpuResourceLayoutDescription[] ResourceLayouts);
}

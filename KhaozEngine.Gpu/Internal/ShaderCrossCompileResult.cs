namespace KhaozEngine.Gpu.Internal
{
    /// <summary>One declared resource, in the LAYOUT's coordinates rather than the module's raw ones.</summary>
    /// <param name="Set">The layout's position in the reflected layout array, which is also the module's
    /// <c>DescriptorSet</c> decoration.</param>
    /// <param name="Binding">The element's POSITION within that layout, which is NOT the raw <c>Binding</c>
    /// decoration whenever the GLSL leaves a gap in its binding numbers: <c>SpirvCrossReflect</c> folds a set's
    /// bindings into dense positions, and every backend indexes the layout positionally.</param>
    internal readonly record struct MslResourceRef(int Set, int Binding);

    /// <summary>
    /// WHICH DECLARED RESOURCES ONE EMITTED MSL STAGE ACTUALLY CARRIES AN ARGUMENT FOR, asked of SPIRV-Cross
    /// after the emission (<see cref="MslIndexRemap.UsedBy"/>).
    /// <para>
    /// IT IS THE HALF OF THE BINDING TABLE THE SCHEME CANNOT DERIVE. Where each element landed is authored, so it
    /// is a pure function of the reflected layouts. WHETHER a given stage reads it at all is a property of the
    /// shader source, and this is the only thing that answers it without re-reading the emitted text.
    /// </para>
    /// </summary>
    /// <param name="Stage">Which stage this is.</param>
    /// <param name="Used">The resources that stage emitted an argument for, in ascending
    /// <c>(set, binding)</c>.</param>
    internal readonly record struct MslStageUse(GpuShaderStages Stage, MslResourceRef[] Used);

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
    /// <param name="MslUse">One entry per stage on the MSL path, EMPTY on the HLSL path. The Direct3D 11 backend
    /// binds a register per layout element for both stages and asks nothing per stage, so there is nothing for it
    /// to carry. The native Metal backend builds its binding table out of this plus the authored scheme.</param>
    internal readonly record struct CrossCompiledPair(
        string VertexSource,
        string FragmentSource,
        ShaderReflection Reflection,
        MslStageUse[] MslUse);

    /// <summary>The compute sibling of <see cref="CrossCompiledPair"/>: one emitted stage plus its
    /// reflection.</summary>
    /// <param name="ComputeSource">The emitted compute-stage source, in the emitting member's target
    /// language.</param>
    /// <param name="Reflection">The reflected resource layouts. <see cref="ShaderReflection.VertexElements"/> is
    /// empty for a compute module.</param>
    /// <param name="MslUse">The single compute stage's entry on the MSL path, EMPTY on the HLSL path. Same
    /// meaning as <see cref="CrossCompiledPair.MslUse"/>.</param>
    internal readonly record struct CrossCompiledCompute(
        string ComputeSource,
        ShaderReflection Reflection,
        MslStageUse[] MslUse);

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

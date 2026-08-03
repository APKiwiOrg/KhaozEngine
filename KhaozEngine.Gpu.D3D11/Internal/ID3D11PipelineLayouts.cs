namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// WHAT A PIPELINE ANSWERS WHEN THE BIND FLUSH ASKS WHERE A SET'S REGISTERS START: its resource layouts, in
    /// PIPELINE-ARRAY order, which is the order decision S2 flattens the sets in. The third internal capability
    /// seam in this package, after <see cref="ID3D11PipelineState"/> and <see cref="ID3D11RingBacked"/>, and it
    /// exists for the reasons those two do.
    /// <para>
    /// SEPARATE FROM <see cref="ID3D11PipelineState"/> ON PURPOSE. That interface is the seven pipeline-level
    /// objects the redundancy caches of decision R6 compare, and its own remarks say it is for that and nothing
    /// else. This one is asked a different question by a different caller, and a COMPUTE pipeline has to answer it
    /// while having none of those seven objects, so folding the layouts into the state seam would force a compute
    /// pipeline to implement six members it has no answer for.
    /// </para>
    /// <para>
    /// THE ARRAY IS THE AUTHORITY ON THE BASE, and the slot a set is bound at indexes into it. A GLSL
    /// <c>set = N</c> number decides nothing: <c>SpriteBatch</c> declares its texture and sampler at
    /// <c>set = 0</c> and its view-projection UBO at <c>set = 1</c>, so any rule phrased as "set 0 comes first" is
    /// already false in shipped code.
    /// </para>
    /// <para>
    /// A PIPELINE THAT DOES NOT IMPLEMENT THIS DECLARES NO LAYOUTS, which the flush treats as an empty array
    /// rather than an error. Binding a set under such a pipeline then fails at the flush with the register
    /// scheme's own "the set index addresses the pipeline's resource-layout array" message, which names the actual
    /// mismatch, instead of failing at the pipeline bind with a message about an interface.
    /// </para>
    /// </summary>
    internal interface ID3D11PipelineLayouts
    {
        /// <summary>The pipeline's resource layouts, in pipeline-array order. Never null.</summary>
        D3D11ResourceLayout[] ResourceLayouts { get; }
    }
}

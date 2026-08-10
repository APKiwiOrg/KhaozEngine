namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHICH ARGUMENT TABLE A BIND WRITES INTO. Metal gives every encoder one table per stage and every setter
    /// names its stage in the selector (<c>setVertexBuffers:</c>, <c>setFragmentTextures:</c>,
    /// <c>setBuffers:</c> on a compute encoder), so the stage is a parameter here rather than a flag anywhere
    /// else.
    /// <para>
    /// THE TABLES ARE ABSOLUTE AND PER ENCODER, which is the fact M-R9 rests on: a bound resource survives a
    /// pipeline switch, and what a pipeline switch can invalidate is only the mapping from an element to an
    /// INDEX in one of these tables. It does not survive an encoder boundary, which is M-R4.
    /// </para>
    /// <para>
    /// THERE IS NO ALL-STAGES MEMBER, deliberately. The engine's shipped sets bind to the stages their layout
    /// declares visible, and a bind emitted for a stage the program does not read is a native call bought for
    /// nothing, which is one half of the #418 fan-out defect this backend exists not to reproduce.
    /// </para>
    /// </summary>
    internal enum MetalShaderStage
    {
        /// <summary>The vertex stage of a render encoder.</summary>
        Vertex = 0,

        /// <summary>The fragment stage of a render encoder.</summary>
        Fragment = 1,

        /// <summary>A compute encoder's single stage. Its setters carry no stage word in the selector, which is
        /// why this is a distinct member rather than a reuse of <see cref="Vertex"/>.</summary>
        Compute = 2,
    }
}

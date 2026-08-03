namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE READ HALF OF THE COMPLETION TIMELINE, and the mirror of <see cref="ID3D11SubmitSignal"/>: one member,
    /// no waiting, no signalling. <see cref="D3D11FenceSubsystem"/> is its one shipped implementation and the
    /// constant-buffer ring allocator is its one consumer.
    /// <para>
    /// WHY THE RING TAKES THIS AND NOT THE TIMELINE. Reading <see cref="ID3D11FenceTimeline.CompletedValue"/>
    /// directly would work on the primary mechanism and be wrong on the fallback, whose poll runs on the immediate
    /// context and has to be serialised against submission. The subsystem is where that decision already lives,
    /// along with the one after device death (a destroyed device answers with everything it ever issued, so a
    /// segment wait cannot outlive its device). A ring that read the timeline would have to reproduce both, in a
    /// type whose subject is memory rather than synchronisation.
    /// </para>
    /// <para>
    /// WHY IT IS NOT A SUBMIT RECEIPT, which is the whole reason work-breakdown row 8 waited on row 13a. Veldrid's
    /// Direct3D 11 fence is set the instant <c>ExecuteCommandList</c> returns, so a ring recycling against one
    /// would hand out a segment the moment the CPU finished ASKING for the work rather than when the GPU finished
    /// DOING it, and the next frame would overwrite uniforms a draw in flight is still reading. That failure is
    /// silent, intermittent and looks like a rendering bug several frames away from its cause. A counter the GPU
    /// itself advances is the only thing a segment may be recycled against.
    /// </para>
    /// </summary>
    internal interface ID3D11CompletionRead
    {
        /// <summary>
        /// The highest completion value the GPU has reached, as a NON-BLOCKING poll. Monotonic, and it may lag
        /// reality (a completed value reads as completed no earlier than the next poll, never later), which is
        /// the safe direction for a segment gate: lagging costs a wait that was not needed and can never hand out
        /// a segment early.
        /// </summary>
        ulong CompletedValue { get; }
    }
}

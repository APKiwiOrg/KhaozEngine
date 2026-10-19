namespace KhaozEngine.Gpu;

/// <summary>A nonblocking consumer retaining a frame fence until its staging entries are released.</summary>
public interface IGpuFrameCompletionConsumer
{
    /// <summary>Whether any staging entry still retains this exact fence. Disposed consumers answer false.</summary>
    bool HasPending(IGpuFence completion);
    /// <summary>Discard entries from a recording known never to have been submitted.</summary>
    void DiscardUnsubmitted(IGpuFence completion);
    /// <summary>Teardown only. The caller proves GPU completion, by a signal or the host teardown drain,
    /// before releasing entries. Diagnostic payloads may be discarded.</summary>
    void ReleaseCompleted(IGpuFence completion);
}

/// <summary>Receives the submission decision independently of diagnostic retention capacity.</summary>
public interface IGpuFrameSubmissionParticipant
{
    /// <summary>The caller's command list was successfully submitted.</summary>
    void Submitted();
    /// <summary>The recording was abandoned or its submission failed. Discard pending logical state.</summary>
    void Abandoned();
}

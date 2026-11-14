namespace KhaozEngine.Gpu;

/// <summary>Rules that refuse GpuRecordingState operations.</summary>
public enum GpuRecordingStateRefusal
{
    /// <summary>Refuses Begin while a command list is already recording.</summary>
    BeginWhileRecording,

    /// <summary>Refuses a command that requires an open recording.</summary>
    CommandOutsideRecording,

    /// <summary>Refuses End before a recording has begun or after a failed recording was abandoned.</summary>
    EndWithoutRecording,

    /// <summary>Refuses End when the recording is already sealed.</summary>
    RecordingAlreadySealed,

    /// <summary>Refuses submission of a command list without a sealed recording.</summary>
    SubmissionWithoutSeal,

    /// <summary>Refuses Draw or DrawIndexed without a graphics pipeline bound in the current recording.
    /// Carries Backend and Command, naming Draw or DrawIndexed.</summary>
    GraphicsPipelineNotBound,

    /// <summary>Refuses Dispatch without a compute pipeline bound in the current recording.
    /// Carries Backend and Command, naming Dispatch.</summary>
    ComputePipelineNotBound,
}

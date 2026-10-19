using System;
using System.Runtime.ExceptionServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>The scene's cold-created submission participant, independent of diagnostic rows and capacity.</summary>
internal sealed partial class CoverageSubmissionState : IGpuFrameSubmissionParticipant
{
    internal CoverageOutcomeReadbacks? Outcomes;
    internal Scene3D? Scene;
    internal CoverageVerificationReadbacks? Verification;

    public void Submitted() => Notify(true);
    public void Abandoned() => Notify(false);

    void Notify(bool success)
    {
        ExceptionDispatchInfo? error = null;
        try { if (success) Scene?.CommitCoverageMotion(); else Scene?.AbandonCoverageMotion(); }
        catch (Exception failure) { error = ExceptionDispatchInfo.Capture(failure); }
        for (var state = Outcomes; state is not null; state = state.Next) NotifyConsumer(state);
        for (var state = Verification; state is not null; state = state.Next) NotifyConsumer(state);
        error?.Throw();
        void NotifyConsumer(IGpuFrameSubmissionParticipant consumer)
        {
            try { if (success) consumer.Submitted(); else consumer.Abandoned(); }
            catch (Exception failure) { error ??= ExceptionDispatchInfo.Capture(failure); }
        }
    }
}

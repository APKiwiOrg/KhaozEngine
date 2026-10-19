using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>The scene's cold-created submission participant, independent of diagnostic rows and capacity.</summary>
internal sealed partial class CoverageSubmissionState : IGpuFrameSubmissionParticipant
{
    internal CoverageOutcomeReadbacks? Outcomes;
    internal Scene3D? Scene;

    public void Submitted()
    {
        Scene?.CommitCoverageMotion();
        for (var state = Outcomes; state is not null; state = state.Next) state.Submitted();
    }

    public void Abandoned()
    {
        Scene?.AbandonCoverageMotion();
        for (var state = Outcomes; state is not null; state = state.Next) state.Abandoned();
    }
}

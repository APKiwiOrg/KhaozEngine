using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>The scene's cold-created submission participant, independent of diagnostic rows and capacity.</summary>
internal sealed partial class CoverageSubmissionState : IGpuFrameSubmissionParticipant
{
    internal CoverageOutcomeReadbacks? Outcomes;

    public void Submitted()
    {
        for (var state = Outcomes; state is not null; state = state.Next) state.Submitted();
    }

    public void Abandoned()
    {
        for (var state = Outcomes; state is not null; state = state.Next) state.Abandoned();
    }
}

using System.Linq;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfVerdict
{
    internal static void RequireSuccess(RendererPerfUnitOutcome outcome)
    {
        var report = outcome.Report;
        bool completed = report is not null && report.Publication.State == RendererPerfPublicationState.Completed
            && report.Publication.ReturnedByRun && report.Publication.FinalUnitPublished;
        bool passed = report?.Manifest.ExecutionClass == RendererPerfExecutionClass.Rehearsal
            ? completed && outcome.Certified && outcome.Failures.IsEmpty && !report.Assessment.ValidForPromotion
                && report.Assessment.Reasons.SequenceEqual([RendererPerfRehearsalPolicy.Reason])
                && report.Compiler.State == RendererPerfCollectionState.Complete && report.Compiler.Errors.IsEmpty
                && report.Publication.OriginalFailure is null && report.Publication.Errors.IsEmpty
                && report.Publication.FailureText.Complete && RendererPerfRehearsalPolicy.FramesPassed(report)
            : completed && report!.Assessment.ValidForPromotion;
        RendererPerfManifest.Require(passed, "Verdict", report?.Publication.OriginalFailure
            ?? "The execution class did not satisfy its terminal evidence verdict.");
    }
}

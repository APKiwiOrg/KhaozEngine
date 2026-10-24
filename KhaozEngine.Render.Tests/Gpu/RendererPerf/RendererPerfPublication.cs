using System;
using System.Collections.Immutable;
using System.IO;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// A separate partial artifact survives a final write failure. Publication never starts another measurement.
internal sealed partial class RendererPerfPublicationWriter(Action<RendererPerfReport, bool> write,
    Action<RendererPerfReport> recordCompletion, Func<RendererPerfReport, RendererPerfReport>? prepare = null, string? directory = null,
    Action<string, byte[]>? writeFailure = null) : IRendererPerfPublication
{
    internal static RendererPerfPublicationWriter Files(string directory, Func<RendererPerfReport, RendererPerfReport>? prepare = null)
    {
        Directory.CreateDirectory(directory);
        var files = new RendererPerfPublicationFiles(directory);
        return new(files.Write, files.RecordCompletion, prepare, directory) { _files = files };
    }

    public RendererPerfReport Publish(RendererPerfReport report, RendererPerfClock clock, long deadline)
    {
        long publicationStart = clock.Now();
        bool finalAttempted = false;
        RendererPerfReport partial = Failure(report, report.Publication.OriginalFailure ?? "Final publication pending.", null);
        try
        {
            report = prepare?.Invoke(report) ?? report;
            partial = Failure(UpdateElapsed(report), report.Publication.OriginalFailure ?? "Final publication pending.", null);
            // Retain raw evidence before checking a deadline that may already have expired.
            write(partial, false);
            CheckDeadline();
            if (report.Publication.OriginalFailure is not null)
                return partial;
            RendererPerfReport completed = report with
            {
                Publication = report.Publication with
                {
                    State = RendererPerfPublicationState.Completed,
                    ReturnedByRun = true,
                    FinalUnitPublished = true,
                    OriginalFailure = null,
                    Errors = []
                }
            };
            completed = UpdateElapsed(completed);
            finalAttempted = true;
            write(completed, true);
            long completedAt = clock.Now();
            if (completedAt >= deadline) throw new TimeoutException("UnitTimeout during publication.");
            completed = UpdateElapsed(completed, completedAt);
            recordCompletion(completed);
            CheckDeadline();
            return completed;
        }
        catch (Exception error)
        {
            string original = report.Publication.OriginalFailure ?? error.Message;
            RendererPerfReport failed = Failure(UpdateElapsed(report), original,
                report.Publication.OriginalFailure is null ? null : error.Message);
            // A late or failed final must not leave an apparently completed artifact. This is bounded
            // recovery publication only, outside the expired measurement budget, with no rerun.
            if (finalAttempted)
            {
                try { write(failed, true); }
                catch (Exception correction) { failed = Failure(failed, original, correction.Message); }
            }
            try { write(failed, false); }
            catch (Exception recovery) { failed = Failure(failed, original, recovery.Message); }
            return failed;
        }

        void CheckDeadline()
        {
            if (clock.Now() >= deadline) throw new TimeoutException("UnitTimeout during publication.");
        }
        RendererPerfReport UpdateElapsed(RendererPerfReport value, long? completedAt = null)
        {
            double elapsed = report.Publication.ElapsedMilliseconds.Value ?? 0;
            elapsed += ((completedAt ?? clock.Now()) - publicationStart) * 1000d / clock.Frequency;
            return value with
            {
                Publication = value.Publication with
                {
                    ElapsedMilliseconds = RendererPerfDriver.Value(elapsed, "ms", "unit monotonic clock including publication"),
                    EstimatedCostUsd = RendererPerfDriver.Value(elapsed / 60000d * (double)report.Manifest.Budget.RatePerMinute,
                        "USD", "elapsed running time estimate, excludes external setup and teardown")
                }
            };
        }
    }

    static RendererPerfReport Failure(RendererPerfReport report, string original, string? error) => report with
    {
        Assessment = report.Assessment with
        {
            ValidForPromotion = false,
            Reasons = report.Assessment.Reasons.Contains(original) ? report.Assessment.Reasons : report.Assessment.Reasons.Add(original)
        },
        Publication = report.Publication with
        {
            State = RendererPerfPublicationState.Failed,
            ReturnedByRun = true,
            FinalUnitPublished = false,
            OriginalFailure = original,
            Errors = error is null ? report.Publication.Errors : report.Publication.Errors.Add(error)
        }
    };
}

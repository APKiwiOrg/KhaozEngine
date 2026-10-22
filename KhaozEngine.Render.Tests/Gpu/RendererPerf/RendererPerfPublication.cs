using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// A separate partial artifact survives a final write failure. Publication never starts another measurement.
internal sealed class RendererPerfPublicationWriter(Action<RendererPerfReport, bool> write) : IRendererPerfPublication
{
    internal static RendererPerfPublicationWriter Files(string directory)
    {
        Directory.CreateDirectory(directory);
        return new((report, final) =>
        {
            byte[] json = RendererPerfReportJson.Serialize(report);
            string stem = Path.Combine(directory, report.Manifest.UnitId + (final ? "" : ".partial"));
            Atomic(stem + ".json", json);
            string markdown = $"# Renderer performance unit {report.Manifest.UnitId}\n\n"
                + $"State {report.Publication.State}. Promotion {report.Assessment.ValidForPromotion}.\n\n"
                + $"Blocks {report.Blocks.Length}/{report.Manifest.Blocks.Length}. Controls {report.Controls.Length}/{report.Manifest.Controls.Length}.\n\n"
                + $"{report.Publication.OriginalFailure}\n"
                + string.Join("\n", report.Assessment.Reasons) + "\n";
            Atomic(stem + ".md", Encoding.UTF8.GetBytes(markdown));
        });
    }

    static void Atomic(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    public RendererPerfReport Publish(RendererPerfReport report, RendererPerfClock clock, long deadline)
    {
        long publicationStart = clock.Now();
        bool finalAttempted = false;
        RendererPerfReport partial = Failure(report, report.Publication.OriginalFailure ?? "Final publication pending.", null);
        try
        {
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
        RendererPerfReport UpdateElapsed(RendererPerfReport value)
        {
            double elapsed = report.Publication.ElapsedMilliseconds.Value ?? 0;
            elapsed += (clock.Now() - publicationStart) * 1000d / clock.Frequency;
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

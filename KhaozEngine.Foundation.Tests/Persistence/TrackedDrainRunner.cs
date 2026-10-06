using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;

namespace KhaozEngine.Tests;

// One named cleanup action. Each step runs whether or not earlier steps failed.
internal readonly record struct TrackedDrainCleanupStep(string Name, Func<Task> Run);

// Runs the tracked drain scenario body, then every cleanup step independently, then emits one report.
// The body's exception is the primary error and is rethrown through ExceptionDispatchInfo, so its type,
// instance and original throw site survive. Cleanup failures are secondary evidence and never replace
// it. A successful body with any failed cleanup still fails. Only when the report itself cannot be
// emitted does the primary travel inside an AggregateException, so no failure is ever silent.
internal static class TrackedDrainRunner
{
    public static async Task RunAsync(TrackedDrainTrace trace, Func<Task> body,
        IReadOnlyList<TrackedDrainCleanupStep> cleanup, Action<string> emit)
    {
        ExceptionDispatchInfo? primary = null;
        trace.Record("body", "start");
        try
        {
            await body();
            trace.Record("body", "completed");
        }
        catch (Exception ex)
        {
            primary = ExceptionDispatchInfo.Capture(ex);
            trace.Record("body", $"failed {ex.GetType().FullName}: {ex.Message}");
        }

        var failures = new List<(string Step, Exception Error)>();
        foreach (TrackedDrainCleanupStep step in cleanup)
        {
            trace.Record("cleanup", $"{step.Name} start");
            try
            {
                await step.Run();
                trace.Record("cleanup", $"{step.Name} completed");
            }
            catch (Exception ex)
            {
                failures.Add((step.Name, ex));
                trace.Record("cleanup", $"{step.Name} failed {ex.GetType().FullName}: {ex.Message}");
            }
        }

        string report = Render(trace, primary?.SourceException, failures);
        Exception? emitFailure = null;
        try
        {
            emit(report);
        }
        catch (Exception ex)
        {
            emitFailure = ex;
        }

        if (primary is not null)
        {
            if (emitFailure is null)
            {
                primary.Throw();
            }

            throw new AggregateException("The scenario failed and its report could not be emitted." + Environment.NewLine + report,
                new[] { primary.SourceException, emitFailure }.Concat(failures.Select(failure => failure.Error)));
        }

        if (failures.Count > 0 || emitFailure is not null)
        {
            IEnumerable<Exception> errors = failures.Select(failure => failure.Error);
            if (emitFailure is not null)
            {
                errors = errors.Append(emitFailure);
            }

            throw new AggregateException("The scenario body succeeded but cleanup or report emission failed." + Environment.NewLine + report, errors);
        }
    }

    private static string Render(TrackedDrainTrace trace, Exception? primary, List<(string Step, Exception Error)> failures)
    {
        var text = new StringBuilder();
        text.AppendLine("tracked drain report");
        text.AppendLine("Late WriteFailed delivery after final drain may be absent from this report. A missing notification entry does not prove it was never delivered.");
        text.Append("primary: ").AppendLine(primary is null ? "none" : primary.ToString());
        text.Append("cleanup failures: ").AppendLine(failures.Count.ToString(CultureInfo.InvariantCulture));
        foreach ((string step, Exception error) in failures)
        {
            text.Append('[').Append(step).Append("] ").AppendLine(error.ToString());
        }

        text.Append(trace.Render());
        return text.ToString();
    }
}

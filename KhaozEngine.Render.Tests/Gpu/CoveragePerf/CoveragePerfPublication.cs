using System;
using System.IO;
using System.Text;

namespace KhaozEngine.Tests.Gpu;

// Temporary files live beside the final files so each rename is atomic on the destination filesystem.
internal sealed class CoveragePerfPublication : IDisposable
{
    readonly string _json, _markdown, _jsonTemporary, _markdownTemporary;

    internal CoveragePerfPublication(string directory, string unit)
    {
        Directory.CreateDirectory(directory);
        _json = Path.Combine(directory, unit + ".json");
        _markdown = Path.Combine(directory, unit + ".md");
        string suffix = "." + Guid.NewGuid().ToString("N") + ".tmp";
        _jsonTemporary = _json + suffix;
        _markdownTemporary = _markdown + suffix;
        if (File.Exists(_json) || File.Exists(_markdown))
            throw new IOException("A final report already exists. Each approval needs a fresh output directory.");
    }

    internal void StageCandidate(CoveragePerfReport candidate)
    {
        // Charge serialization and disk writes to Run's publication deadline, without exposing a final candidate.
        File.WriteAllText(_jsonTemporary, CoveragePerfReportJson.Serialize(candidate), new UTF8Encoding(false));
        File.WriteAllText(_markdownTemporary, CoveragePerfReportJson.Markdown(candidate), new UTF8Encoding(false));
    }

    internal CoveragePerfReport PublishReturned(CoveragePerfReport report, CoveragePerfClock clock, long start)
    {
        try
        {
            CoveragePerfReportJson.WriteReturnedReport(report, _jsonTemporary, _markdownTemporary);
            if (report.Manifest is { } manifest &&
                (clock.Now() - start) / (double)clock.Frequency > manifest.MaxUnitSeconds)
            {
                const string error = "Final report publication exceeded the cooperative unit deadline.";
                report = report with
                {
                    Status = CoveragePerfRunStatus.Failed,
                    Reason = report.Errors.IsEmpty ? error : report.Reason,
                    Errors = report.Errors.Add(error),
                    FinalUnitPublished = false,
                    CpuStatus = CoveragePerfBudgetStatus.Unproved,
                    GpuStatus = CoveragePerfBudgetStatus.Unproved,
                };
                CoveragePerfReportJson.WriteReturnedReport(report, _jsonTemporary, _markdownTemporary);
            }
            File.Move(_markdownTemporary, _markdown);
            // JSON is the final marker, visible only after the Markdown rename succeeded.
            File.Move(_jsonTemporary, _json);
            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(report.Reason + " Final report publication failed: " + ex.Message, ex);
        }
    }

    public void Dispose()
    {
        File.Delete(_jsonTemporary);
        File.Delete(_markdownTemporary);
    }
}

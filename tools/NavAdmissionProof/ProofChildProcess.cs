using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.Movement.Research;

internal static class ProofChildProcess
{
    internal static async Task<ChildAttempt> Run(string directory, string label, bool stable, PairEvidence pair)
    {
        var attempt = new ChildAttempt { Label = label, UseStableLegacyReadScope = stable };
        pair.Samples.Add(attempt);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(ProofChildProcess).Assembly.Location);
        start.ArgumentList.Add("--child");
        start.ArgumentList.Add(label);
        start.ArgumentList.Add(stable ? "true" : "false");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(pair.RunId);
        start.Environment["DOTNET_TieredCompilation"] = "0";
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["KHAOZ_NAV_PROOF_CHILD"] = pair.RunId;
        using var process = new Process { StartInfo = start };
        var elapsed = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(pair.ChildTimeoutSeconds));
        Task? output = null, errorOutput = null;
        string outputPath = Path.Combine(directory, label + ".stdout.log");
        string errorPath = Path.Combine(directory, label + ".stderr.log");
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Child process did not start.");
            attempt.ProcessId = process.Id;
            output = Drain(process.StandardOutput, outputPath);
            errorOutput = Drain(process.StandardError, errorPath);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                attempt.TimedOut = true;
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            attempt.ExitStatus = process.ExitCode;
            await Task.WhenAll(output, errorOutput).WaitAsync(TimeSpan.FromSeconds(10));
            if (attempt.TimedOut || attempt.ExitStatus != 0)
                throw new InvalidOperationException("Child " + label + " failed or exceeded its three-minute limit. No retry is allowed.");
            ChildResult result = ProofJson.ReadChild(Path.Combine(directory, label + ".json"));
            attempt.Result = result;
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, label + ".kenb"));
            if (result.RunId != pair.RunId || result.Label != label || result.UseStableLegacyReadScope != stable ||
                result.Fixture != ProofFixture.Identity || !result.CanonicalRoundTrip || !result.ScopeReleasedAfterBuild ||
                result.Graph is null || result.Environment is null || result.Metrics is null || bytes.Length < 52 ||
                !bytes.AsSpan(0, 4).SequenceEqual("KENB"u8) ||
                result.KenbLength != bytes.Length || result.KenbSha256 != Convert.ToHexString(SHA256.HashData(bytes)))
                throw new InvalidDataException("Child evidence is missing, malformed or inconsistent with its KENB file.");
            ProfileMetrics metrics = result.Metrics;
            if (!double.IsFinite(metrics.WallSeconds) || metrics.WallSeconds <= 0 ||
                !double.IsFinite(metrics.ProcessCpuSeconds) || metrics.ProcessCpuSeconds <= 0 ||
                !double.IsFinite(metrics.MainThreadCpuSeconds) || metrics.MainThreadCpuSeconds <= 0 ||
                metrics.ManagedProcessAllocatedBytes < 0 || metrics.ManagedThreadAllocatedBytes < 0 ||
                metrics.Gen0Collections < 0 || metrics.Gen1Collections < 0 || metrics.Gen2Collections < 0 ||
                metrics.MonitorLockContentions < 0)
                throw new InvalidDataException("Invalid profile measurement deltas.");
            return attempt;
        }
        catch (Exception error)
        {
            attempt.Failure = error.ToString();
            throw;
        }
        finally
        {
            if (attempt.ProcessId is not null && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    attempt.ExitStatus = process.ExitCode;
                }
                catch (Exception error) { attempt.Failure += "\nTermination failed: " + error; }
            }
            if (output is not null && errorOutput is not null)
            {
                try { await Task.WhenAll(output, errorOutput).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { attempt.Failure += "\nDiagnostic drain failed: " + error; }
            }
            attempt.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
            // Logs are flushed as they arrive, including progress preceding a timeout or error.
            if (File.Exists(outputPath)) attempt.StandardOutput = File.ReadAllText(outputPath);
            if (File.Exists(errorPath)) attempt.StandardError = File.ReadAllText(errorPath);
        }
    }

    private static async Task Drain(StreamReader source, string path)
    {
        using var destination = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
        while (await source.ReadLineAsync() is { } line) await destination.WriteLineAsync(line);
    }
}

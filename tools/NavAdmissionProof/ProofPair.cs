using System;
using System.IO;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.Movement.Research;

internal static class ProofPair
{
    internal static async Task Run(string path, string directory, PairEvidence pair,
        Func<string, bool, Task<ChildAttempt>> run)
    {
        try
        {
            ProofJson.WriteEnvelope(path, pair);
            // Serial, with an explicit success gate before B even if a runner returns partial evidence.
            ChildAttempt a = await run("A", false);
            ProofChildAcceptance.RequireCompleted(a);
            ProofJson.WriteEnvelope(path, pair);
            ChildAttempt b = await run("B", true);
            ProofChildAcceptance.RequireCompleted(b);
            ProofJson.WriteEnvelope(path, pair);
            byte[] left = File.ReadAllBytes(Path.Combine(directory, "A.kenb"));
            byte[] right = File.ReadAllBytes(Path.Combine(directory, "B.kenb"));
            ChildResult aResult = a.Result!, bResult = b.Result!;
            pair.CompleteKenbBytesEqual = left.AsSpan().SequenceEqual(right);
            pair.GraphCountsEqual = aResult.Graph == bResult.Graph;
            pair.SameBinarySet = aResult.Environment == bResult.Environment &&
                aResult.Environment.BinarySetSha256 == pair.Environment!.BinarySetSha256 &&
                aResult.Environment.Commit == pair.Environment.Commit;
            if (!pair.CompleteKenbBytesEqual || !pair.GraphCountsEqual || !pair.SameBinarySet)
                throw new InvalidOperationException("The ordered pair failed byte, graph or binary identity parity.");
            pair.CpuRatioBOverA = bResult.Metrics!.ProcessCpuSeconds / aResult.Metrics!.ProcessCpuSeconds;
            pair.MainThreadCpuRatioBOverA = bResult.Metrics.MainThreadCpuSeconds / aResult.Metrics.MainThreadCpuSeconds;
            pair.WallRatioBOverA = bResult.Metrics.WallSeconds / aResult.Metrics.WallSeconds;
            pair.Outcome = "Parity passed. CPU evidence requires owner review against the documented decision rule.";
        }
        catch (Exception error)
        {
            pair.Outcome = "Failed";
            pair.Failure = error.ToString();
            throw;
        }
        finally
        {
            ProofJson.WriteEnvelope(path, pair);
            PairEvidence retained = ProofJson.ReadEnvelope(path);
            if (retained.RunId != pair.RunId || retained.Samples.Count != pair.Samples.Count ||
                retained.Outcome != pair.Outcome)
                throw new InvalidDataException("The retained pending artifact does not match this invocation.");
        }
    }
}

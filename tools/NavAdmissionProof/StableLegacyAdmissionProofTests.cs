using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace KhaozEngine.Tests.Movement.Research;

[CollectionDefinition("HostedNavAdmissionProof", DisableParallelization = true)]
public sealed class HostedNavAdmissionProofCollection;

[Collection("HostedNavAdmissionProof")]
public sealed class StableLegacyAdmissionProofTests
{
    [Fact]
    public async Task SingleUnprofiledPair()
    {
        ProofHost.RequireHostedSelection();
        string path = ProofHost.Required("KHAOZ_SUPPORT_COST_ARTIFACT");
        if (File.Exists(path)) throw new InvalidOperationException("Refusing to overwrite an existing proof artifact.");
        var pair = new PairEvidence();
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "nav-admission-" + pair.RunId);
        Directory.CreateDirectory(directory);
        try
        {
            pair.Environment = ProofHost.Describe();
            ProofJson.WriteEnvelope(path, pair);
            // Serial and deliberately explicit. A failure exits this block before B can start.
            ChildAttempt a = await ProofChildProcess.Run(directory, "A", false, pair);
            ProofJson.WriteEnvelope(path, pair);
            ChildAttempt b = await ProofChildProcess.Run(directory, "B", true, pair);
            ProofJson.WriteEnvelope(path, pair);
            byte[] left = File.ReadAllBytes(Path.Combine(directory, "A.kenb"));
            byte[] right = File.ReadAllBytes(Path.Combine(directory, "B.kenb"));
            pair.CompleteKenbBytesEqual = left.AsSpan().SequenceEqual(right);
            pair.GraphCountsEqual = a.Result!.Graph == b.Result!.Graph;
            pair.SameBinarySet = a.Result.Environment == b.Result.Environment &&
                a.Result.Environment.BinarySetSha256 == pair.Environment.BinarySetSha256 &&
                a.Result.Environment.Commit == pair.Environment.Commit;
            if (!pair.CompleteKenbBytesEqual || !pair.GraphCountsEqual || !pair.SameBinarySet)
                throw new InvalidOperationException("The ordered pair failed byte, graph or binary identity parity.");
            pair.CpuRatioBOverA = b.Result.Metrics.ProcessCpuSeconds / a.Result.Metrics.ProcessCpuSeconds;
            pair.MainThreadCpuRatioBOverA = b.Result.Metrics.MainThreadCpuSeconds / a.Result.Metrics.MainThreadCpuSeconds;
            pair.WallRatioBOverA = b.Result.Metrics.WallSeconds / a.Result.Metrics.WallSeconds;
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
        Assert.Equal(2, pair.Samples.Count);
        Assert.All(pair.Samples, attempt =>
        {
            Assert.Equal(0, attempt.ExitStatus);
            Assert.False(attempt.TimedOut);
            Assert.NotNull(attempt.Result);
        });
        Assert.True(pair.CompleteKenbBytesEqual && pair.GraphCountsEqual && pair.SameBinarySet);
        Assert.True(pair.Samples.Select(sample => sample.Label).SequenceEqual(new[] { "A", "B" }));
    }
}

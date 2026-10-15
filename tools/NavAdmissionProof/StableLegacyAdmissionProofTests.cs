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
        pair.Environment = ProofHost.Describe();
        await ProofPair.Run(path, directory, pair,
            (label, stable) => ProofChildProcess.Run(directory, label, stable, pair));
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

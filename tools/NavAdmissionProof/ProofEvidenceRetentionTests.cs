using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace KhaozEngine.Tests.Movement.Research;

public sealed class ProofEvidenceRetentionTests
{
    [Fact]
    public void PartialChildIsExplicitlyIncomplete()
    {
        ChildResult child = NewChild();
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(child, ProofJson.Options));
        Assert.True(json.RootElement.TryGetProperty("Status", out JsonElement status));
        Assert.Equal("Running", status.GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("Metrics").ValueKind);
    }

    [Fact]
    public void SetupFailureRetainsIdentityBeforeWorkBegins()
    {
        using var files = new EvidenceFiles();
        var evidence = new ProofChildEvidence(files.Directory, NewChild());
        var failure = new InvalidOperationException("synthetic setup failure");
        Exception actual = Assert.Throws<InvalidOperationException>(() => evidence.Run(state =>
        {
            ChildResult started = ProofJson.ReadChild(files.ChildPath);
            Assert.Equal("synthetic-invocation", started.RunId);
            Assert.Equal("A", started.Label);
            Assert.False(started.UseStableLegacyReadScope);
            Assert.Equal(SyntheticEnvironment(), started.Environment);
            Assert.Equal("SetupAndCapture", started.Stage);
            Assert.Equal("Running", started.Status);
            Assert.Null(started.Metrics);
            throw failure;
        }));
        Assert.Same(failure, actual);
        ChildResult retained = ProofJson.ReadChild(files.ChildPath);
        Assert.Equal("Failed", retained.Status);
        Assert.Equal("SetupAndCapture", retained.Stage);
        Assert.Contains("synthetic setup failure", retained.Failure!);
    }

    [Theory]
    [InlineData("DimensionsMatch", 10000, 1, 50000, true, true, true, true, 163)]
    [InlineData("EnoughAcceptedNodes", 9999, 1, 50000, true, true, true, true, 164)]
    [InlineData("EnoughDirectedEdges", 10000, 1, 49999, true, true, true, true, 164)]
    [InlineData("HasRejectedLayerCells", 10000, 0, 50000, true, true, true, true, 164)]
    [InlineData("ClearEdgeAccepted", 10000, 1, 50000, false, true, true, true, 164)]
    [InlineData("ClearGroundApproaches", 10000, 1, 50000, true, false, true, true, 164)]
    [InlineData("AdjacentGroundObstructions", 10000, 1, 50000, true, true, false, true, 164)]
    [InlineData("GroundEntriesRefused", 10000, 1, 50000, true, true, true, false, 164)]
    public void GraphFailureRetainsMetricsCountsAndEveryPredicate(string predicate, int accepted,
        int rejected, int edges, bool clear, bool approaches, bool obstruction, bool refused, int width)
    {
        using var files = new EvidenceFiles();
        var evidence = new ProofChildEvidence(files.Directory, NewChild());
        var counts = new GraphCounts(1, accepted, rejected, edges, 3, 2, clear, approaches, obstruction, refused);
        var center = new ObstacleCellEvidence(82, 102, 0.125f, 0.125f, [2f], [new(0, 2f, true)]);
        var approach = new ObstacleCellEvidence(85, 102, 0.875f, 0.125f, [0f], [new(2, 0f, true)]);
        var target = new ObstacleCellEvidence(84, 102, 0.625f, 0.125f, [0f], [new(2, null, false)]);
        var diagnostics = new GroundObstacleEvidence(center,
            [new("synthetic-east", approach, approach, target, 2, approaches, obstruction, refused)]);
        var validation = new GraphAcceptance(counts, [new(0, width, 204)], diagnostics);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => evidence.Run(state =>
        {
            state.Begin("BuildProfile");
            state.RecordMeasurement(SyntheticMetrics());
            // Metrics must already be durable before any graph acceptance work.
            Assert.Equal(SyntheticMetrics(), ProofJson.ReadChild(files.ChildPath).Metrics);
            state.CheckGraph(validation);
        }));
        ChildResult retained = ProofJson.ReadChild(files.ChildPath);
        Assert.Equal(SyntheticMetrics(), retained.Metrics);
        Assert.Equal(counts, retained.Graph);
        Assert.Equal("Failed", retained.Status);
        Assert.Equal("GraphAcceptance", retained.Stage);
        Assert.False(retained.GraphValidation!.Passed);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(files.ChildPath));
        JsonElement predicates = json.RootElement.GetProperty("GraphValidation");
        string[] names = ["DimensionsMatch", "EnoughAcceptedNodes", "EnoughDirectedEdges",
            "HasRejectedLayerCells", "ClearEdgeAccepted", "ClearGroundApproaches",
            "AdjacentGroundObstructions", "GroundEntriesRefused"];
        foreach (string name in names)
            Assert.Equal(name != predicate, predicates.GetProperty(name).GetBoolean());
        Assert.Contains("\"" + predicate + "\": false", failure.Message);
        Assert.Contains("\"AcceptedNodes\": " + accepted, failure.Message);
        Assert.Contains("\"DirectedEdges\": " + edges, retained.Failure!);
        Assert.Equal(width, retained.GraphValidation.LayerDimensions[0].Width);
        GroundObstacleEvidence saved = Assert.IsType<GroundObstacleEvidence>(retained.GraphValidation.GroundObstacle);
        Assert.Equal(2f, Assert.Single(saved.Center.CapturedHeights));
        Assert.Equal(0, Assert.Single(saved.Center.Surfaces).Layer);
        GroundSideEvidence side = Assert.Single(saved.Sides);
        Assert.Equal(2, side.GroundLayer);
        Assert.Equal(0f, Assert.Single(side.Approach.Surfaces).Height);
        Assert.Equal(0f, Assert.Single(side.Obstruction.CapturedHeights));
        Assert.Null(Assert.Single(side.Obstruction.Surfaces).Height);
        Assert.False(Assert.Single(side.Obstruction.Surfaces).Accepted);
        Assert.Equal(approaches, side.ClearGroundApproach);
        Assert.Equal(obstruction, side.AdjacentGroundObstruction);
        Assert.Equal(refused, side.GroundEntryRefused);
        Assert.Null(retained.KenbLength);
        Assert.False(File.Exists(files.KenbPath));
    }

    [Theory]
    [InlineData("CanonicalRoundTrip", false)]
    [InlineData("ScopeRelease", true)]
    public void LaterFailureRetainsCompletedCanonicalBytes(string stage, bool roundTrip)
    {
        using var files = new EvidenceFiles();
        var evidence = new ProofChildEvidence(files.Directory, NewChild());
        byte[] bytes = SyntheticKenb();
        Assert.Throws<InvalidOperationException>(() => evidence.Run(state =>
        {
            state.RecordMeasurement(SyntheticMetrics());
            state.CheckGraph(AcceptedGraph());
            state.Begin("CanonicalSerialization");
            state.RecordCanonical(bytes);
            Assert.Equal(bytes, File.ReadAllBytes(files.KenbPath));
            Assert.Equal(52, ProofJson.ReadChild(files.ChildPath).KenbLength);
            state.Result.CanonicalRoundTrip = roundTrip;
            state.Begin(stage);
            throw new InvalidOperationException("synthetic later failure");
        }));
        ChildResult retained = ProofJson.ReadChild(files.ChildPath);
        Assert.Equal("Failed", retained.Status);
        Assert.Equal(stage, retained.Stage);
        Assert.Equal(roundTrip, retained.CanonicalRoundTrip);
        Assert.Null(retained.ScopeReleasedAfterBuild);
        Assert.Equal(SyntheticMetrics(), retained.Metrics);
        Assert.Equal(52, retained.KenbLength);
        Assert.Equal("9DDEEAE5A13658B7BD119004DFFF5F31DD24FB5ECD9C9DDA4B8CD39754D61286", retained.KenbSha256);
        Assert.Equal(bytes, File.ReadAllBytes(files.KenbPath));
    }

    [Theory]
    [InlineData(1, false, "Failed", "GraphAcceptance")]
    [InlineData(0, false, "Running", "BuildProfile")]
    [InlineData(0, false, "Failed", "ScopeRelease")]
    [InlineData(0, true, "Succeeded", "Completed")]
    [InlineData(1, false, "Succeeded", "Completed")]
    public async Task IncompleteOrNonzeroChildPreventsBAndRetainsFailedEnvelope(
        int exit, bool timedOut, string status, string stage)
    {
        using var files = new EvidenceFiles();
        var pair = new PairEvidence { RunId = "synthetic-invocation", Environment = SyntheticEnvironment() };
        ChildResult child = CompletedChild("A", false) with { Status = status, Stage = stage };
        ProofJson.Write(files.ChildPath, child);
        bool bStarted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProofPair.Run(files.PairPath, files.Directory, pair,
            (label, stable) =>
            {
                if (label == "B") bStarted = true;
                var attempt = new ChildAttempt
                {
                    Label = label,
                    UseStableLegacyReadScope = stable,
                    ExitStatus = exit,
                    TimedOut = timedOut
                };
                pair.Samples.Add(attempt);
                ProofChildAcceptance.Retain(files.Directory, attempt);
                return Task.FromResult(attempt);
            }));
        Assert.False(bStarted);
        PairEvidence retained = ProofJson.ReadEnvelope(files.PairPath);
        Assert.Equal("Failed", retained.Outcome);
        Assert.Null(retained.ProcessExitStatus);
        ChildAttempt a = Assert.Single(retained.Samples);
        Assert.Equal(exit, a.ExitStatus);
        Assert.Equal(timedOut, a.TimedOut);
        Assert.Equal(SyntheticMetrics(), a.Result!.Metrics);
        Assert.Equal(AcceptedGraph().Counts, a.Result.Graph);
        Assert.Equal(status, a.Result.Status);
        Assert.False(retained.CompleteKenbBytesEqual);
        Assert.False(retained.GraphCountsEqual);
        Assert.False(retained.SameBinarySet);
        Assert.Null(retained.CpuRatioBOverA);
        Assert.Null(retained.MainThreadCpuRatioBOverA);
        Assert.Null(retained.WallRatioBOverA);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(files.PairPath));
        Assert.Equal("synthetic-invocation", json.RootElement.GetProperty("pendingFinalization")
            .GetProperty("RunId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrMalformedChildEvidencePreventsB(bool malformed)
    {
        using var files = new EvidenceFiles();
        if (malformed) File.WriteAllText(files.ChildPath, "{not-json");
        var pair = new PairEvidence { RunId = "synthetic-invocation", Environment = SyntheticEnvironment() };
        await Assert.ThrowsAsync<InvalidDataException>(() => ProofPair.Run(files.PairPath, files.Directory, pair,
            (label, stable) =>
            {
                Assert.Equal("A", label);
                var attempt = new ChildAttempt { Label = label, UseStableLegacyReadScope = stable, ExitStatus = 0 };
                pair.Samples.Add(attempt);
                ProofChildAcceptance.Retain(files.Directory, attempt);
                return Task.FromResult(attempt);
            }));
        PairEvidence retained = ProofJson.ReadEnvelope(files.PairPath);
        Assert.Equal("Failed", retained.Outcome);
        Assert.NotEmpty(Assert.Single(retained.Samples).EvidenceReadFailure!);
        Assert.Null(retained.CpuRatioBOverA);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("bytes")]
    [InlineData("graph")]
    [InlineData("binary")]
    public async Task CompleteSyntheticPairStillRequiresAllParity(string difference)
    {
        using var files = new EvidenceFiles();
        var pair = new PairEvidence { RunId = "synthetic-invocation", Environment = SyntheticEnvironment() };
        async Task Run() => await ProofPair.Run(files.PairPath, files.Directory, pair, (label, stable) =>
        {
            ChildResult child = CompletedChild(label, stable);
            byte[] bytes = SyntheticKenb();
            if (stable)
            {
                if (difference == "bytes") bytes[^1] = 1;
                if (difference == "graph")
                    child.GraphValidation = new(AcceptedGraph().Counts with { AcceptedLinks = 1 }, [new(0, 164, 204)]);
                if (difference == "binary")
                    child = child with { Environment = SyntheticEnvironment() with { BinarySetSha256 = "different" } };
            }
            File.WriteAllBytes(Path.Combine(files.Directory, label + ".kenb"), bytes);
            var attempt = new ChildAttempt
            {
                Label = label,
                UseStableLegacyReadScope = stable,
                ExitStatus = 0,
                Result = child
            };
            pair.Samples.Add(attempt);
            return Task.FromResult(attempt);
        });
        if (difference == "none") await Run();
        else await Assert.ThrowsAsync<InvalidOperationException>(Run);
        PairEvidence retained = ProofJson.ReadEnvelope(files.PairPath);
        Assert.Equal(new[] { "A", "B" }, retained.Samples.Select(attempt => attempt.Label));
        if (difference == "none")
        {
            Assert.True(retained.CompleteKenbBytesEqual && retained.GraphCountsEqual && retained.SameBinarySet);
            Assert.Equal(1, retained.CpuRatioBOverA);
            Assert.Equal(1, retained.MainThreadCpuRatioBOverA);
            Assert.Equal(1, retained.WallRatioBOverA);
        }
        else
        {
            Assert.Equal("Failed", retained.Outcome);
            Assert.Null(retained.CpuRatioBOverA);
        }
    }

    private static ChildResult NewChild() => new("synthetic-invocation", "A", false,
        ProofFixture.Identity, SyntheticEnvironment());

    private static ChildResult CompletedChild(string label, bool stable) => NewChild() with
    {
        Label = label,
        UseStableLegacyReadScope = stable,
        Metrics = SyntheticMetrics(),
        GraphValidation = AcceptedGraph(),
        KenbLength = 52,
        KenbSha256 = "9DDEEAE5A13658B7BD119004DFFF5F31DD24FB5ECD9C9DDA4B8CD39754D61286",
        CanonicalRoundTrip = true,
        ScopeReleasedAfterBuild = true,
        Stage = "Completed",
        Status = "Succeeded",
    };

    private static ProfileMetrics SyntheticMetrics() => new(1.25, 1, 0.75, 128, 64, 1, 0, 0, 2);
    private static GraphAcceptance AcceptedGraph() => new(
        new GraphCounts(1, 10000, 1, 50000, 3, 2, true, true, true, true), [new(0, 164, 204)]);

    private static byte[] SyntheticKenb()
    {
        var bytes = new byte[52];
        "KENB"u8.CopyTo(bytes);
        return bytes;
    }

    private static ProofEnvironment SyntheticEnvironment() => new(
        ".NET synthetic", "10.0.0", "Synthetic OS", "X64", 1, "10.0.100", "Release",
        "0000000000000000000000000000000000000000", "synthetic", "synthetic-binary-hash", "0",
        null, null, null, null, null, "0", "DOTNET_TieredCompilation=0", "synthetic", "Linux", "X64",
        "synthetic", "synthetic", "synthetic-run", "1");

    private sealed class EvidenceFiles : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "nav-proof-pure-" + Guid.NewGuid().ToString("N"));
        internal string ChildPath => Path.Combine(Directory, "A.json");
        internal string KenbPath => Path.Combine(Directory, "A.kenb");
        internal string PairPath => Path.Combine(Directory, "pair.json");
        internal EvidenceFiles() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

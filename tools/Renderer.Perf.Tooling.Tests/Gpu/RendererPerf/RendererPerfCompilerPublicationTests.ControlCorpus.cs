using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Fact]
    public void Real_control_corpus_passes_noncompiler_certification()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(RendererPerfControlShaders.Corpus, RendererPerfManifest.JsonOptions);
        var expected = new RendererPerfExpectedSidecar(RendererPerfSidecarKind.NonCompiler,
            RendererPerfArtifactProducers.Schema("control-corpus.json"), "control-corpus.json", bytes.Length,
            RendererPerfRecipe.Hash(bytes), RendererPerfSidecarCounts.Empty);

        RendererPerfArtifactProducers.Verify(expected, bytes, RendererPerfAnalysisFixture.Report());
    }

    [Fact]
    public void Real_control_corpus_passes_final_directory_certification()
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        var writer = RendererPerfPublicationWriter.Files(f.Directory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(RendererPerfControlShaders.Corpus, RendererPerfManifest.JsonOptions);
        writer.Produce("control-corpus.json", RendererPerfArtifactProducers.Schema("control-corpus.json"), bytes);

        var result = f.Publish(writer);

        Assert.Empty(result.Failures);
        Assert.True(result.Report.Publication.FinalUnitPublished, result.Report.Publication.OriginalFailure);
        var verified = RendererPerfArtifactVerification.Verify(f.Directory, f.Report.Manifest.UnitId + ".json");
        Assert.Equal(RendererPerfRecipe.Hash(bytes), verified.Report.Sidecars.Single(s => s.Path == "control-corpus.json").Sha256);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("compute-zero")]
    [InlineData("compute-size")]
    [InlineData("graphics-size")]
    [InlineData("hash")]
    [InlineData("store")]
    [InlineData("fma")]
    [InlineData("divide")]
    [InlineData("root")]
    public void Invalid_control_corpus_still_refuses(string fault)
    {
        var corpus = RendererPerfControlShaders.Corpus.ToArray();
        corpus[0] = fault switch
        {
            "duplicate" => corpus[1],
            "unknown" => corpus[0] with { Name = "foreign.comp" },
            "compute-zero" => corpus[0] with { LocalX = 0 },
            "compute-size" => corpus[0] with { LocalX = 2 },
            "hash" => corpus[0] with { HlslSha256 = "invalid" },
            "store" => corpus[0] with { Stores = 0 },
            "fma" => corpus[0] with { FmaInstructions = -1 },
            "divide" => corpus[0] with { Divides = -1 },
            "root" => corpus[0] with { Roots = -1 },
            _ => corpus[0]
        };
        if (fault == "missing") corpus = corpus[..^1];
        if (fault == "graphics-size") corpus[4] = corpus[4] with { LocalX = 1 };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(corpus.ToImmutableArray(), RendererPerfManifest.JsonOptions);
        var expected = new RendererPerfExpectedSidecar(RendererPerfSidecarKind.NonCompiler,
            RendererPerfArtifactProducers.Schema("control-corpus.json"), "control-corpus.json", bytes.Length,
            RendererPerfRecipe.Hash(bytes), RendererPerfSidecarCounts.Empty);

        Assert.ThrowsAny<Exception>(() => RendererPerfArtifactProducers.Verify(expected, bytes, RendererPerfAnalysisFixture.Report()));
    }
}

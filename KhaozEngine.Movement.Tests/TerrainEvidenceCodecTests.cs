using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class TerrainEvidenceCodecTests
{
    [Theory]
    [InlineData("names-count")]
    [InlineData("point-nan")]
    [InlineData("analytic-tag")]
    [InlineData("kind-tag")]
    [InlineData("vertex-count")]
    [InlineData("adjacency")]
    [InlineData("excluded")]
    [InlineData("reference-count")]
    [InlineData("reference")]
    [InlineData("raw-missing")]
    [InlineData("raw-range")]
    [InlineData("trailing")]
    public void MalformedEvidenceIsRejectedBeforeItCanSupplyCandidates(string mutation)
    {
        var (e, columns) = Fixture();
        byte[] payload = TerrainEvidenceWriter.Encode(e, columns);
        var offsets = Offsets(payload);
        if (mutation == "names-count") payload = [255, 255, 255, 255, 15];
        else if (mutation == "point-nan") BitConverter.GetBytes(float.NaN).CopyTo(payload, offsets["point"]);
        else if (mutation == "analytic-tag") payload[offsets["analytic"]] = 2;
        else if (mutation == "kind-tag") payload[offsets["kind"]] = 255;
        else if (mutation == "vertex-count") payload[offsets["vertices"]] = 2;
        else if (mutation == "adjacency") payload[offsets["adjacency"]] = 255;
        else if (mutation == "excluded") payload[offsets["coverage"]] = (byte)DescentCoverage.Excluded;
        else if (mutation == "reference-count") payload[offsets["references"]] = 127;
        else if (mutation == "reference") payload[offsets["reference"]] = 1;
        else if (mutation == "raw-missing") payload[offsets["raw"]] = 0;
        else if (mutation == "raw-range") payload[offsets["raw"]] = 2;
        else payload = [.. payload, 0];
        Assert.Throws<InvalidDataException>(() => TerrainEvidenceDecoder.Decode(payload, e.Options, columns));
    }

    [Fact]
    public void CanonicalEvidenceRetainsExactRawIdentityAndRewritesByteForByte()
    {
        var (e, columns) = Fixture();
        byte[] bytes = TerrainEvidenceWriter.Encode(e, columns);
        var loaded = TerrainEvidenceDecoder.Decode(bytes, e.Options, columns);
        Assert.Equal(bytes, TerrainEvidenceWriter.Encode(loaded, columns));
        Assert.True(loaded.TryRaw(0, 0, 0, out var raw));
        Assert.Equal(new DescentRawBinding(0, 0, 0, 0, 0), raw);
        Assert.True(loaded.StrictDependencies);
    }

    static Dictionary<string, int> Offsets(byte[] payload)
    {
        var r = new TerrainEvidenceReader(payload); var result = new Dictionary<string, int>();
        uint names = r.Unsigned(); for (int i = 0; i < names; i++) _ = r.Text();
        uint points = r.Unsigned();
        for (int i = 0; i < points; i++)
        {
            Assert.True(r.Boolean()); if (i == 0) result["point"] = payload.Length - r.Remaining;
            _ = r.Single(); _ = r.Single(); _ = r.Single();
        }
        Assert.Equal(1u, r.Unsigned());
        _ = r.Unsigned(); _ = r.Address(); _ = r.Unsigned(); _ = r.Unsigned(); _ = r.Unsigned();
        result["analytic"] = payload.Length - r.Remaining; _ = r.Boolean();
        result["kind"] = payload.Length - r.Remaining; _ = r.Byte();
        result["vertices"] = payload.Length - r.Remaining; uint vertices = r.Unsigned();
        for (int i = 0; i < vertices; i++) _ = r.Unsigned();
        result["adjacency"] = payload.Length - r.Remaining; Assert.Equal(0u, r.Unsigned());
        Assert.Equal(1u, r.Unsigned()); _ = r.Unsigned();
        result["coverage"] = payload.Length - r.Remaining; _ = r.Byte();
        result["references"] = payload.Length - r.Remaining; _ = r.Unsigned();
        result["reference"] = payload.Length - r.Remaining; _ = r.Unsigned(); _ = r.Unsigned();
        result["raw"] = payload.Length - r.Remaining;
        return result;
    }

    static (PhysicsNavDescentEvidence, PhysicsNavColumns) Fixture()
    {
        var options = new PhysicsNavBakeOptions(0, 0, 1, 1, 1, 10, 14, 0.7f, 1, 16) { CaptureTerrainDescentEvidence = true };
        var columns = new PhysicsNavColumns(options, 1, 1, [0, 1], [new(0, float.PositiveInfinity, 0)], []);
        DescentFace[] faces = [new(new("ground", 0, 0), new("world", "ground"), true, PhysicsEvidenceKind.Triangle,
            [new(0, 0, 0), new(0, 0, 1), new(1, 0, 0)], default, 0, 0)];
        return (new(options, 1, 1, faces, [0, 1], [new(0, 0)], [DescentCoverage.Complete], [new(0, 0, 0, 0, 0)], [[]]), columns);
    }
}

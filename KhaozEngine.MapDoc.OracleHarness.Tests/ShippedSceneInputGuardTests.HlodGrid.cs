using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Fact]
    public void LegacyHlodGrid_NativeTileSizeCannotChangeSourceRecording()
    {
        using var baseline = ShippedSceneGuardFixtures.Create("roof-hlod");
        using var mutated = ShippedSceneGuardFixtures.Create("roof-hlod-native-tile-size");
        var sourceInputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(baseline.Environment));
        var mutatedInputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(mutated.Environment));
        Assert.Equal(1, sourceInputs.World.TileSize);
        Assert.Equal(sourceInputs.World.TileSize, mutatedInputs.World.TileSize);
        Assert.Equal(1, sourceInputs.NativeTileSize);
        Assert.Equal(512, mutatedInputs.NativeTileSize);
        var expected = LegacySceneRecorder.Record(sourceInputs.World, sourceInputs.Catalogs, sourceInputs);
        var actual = LegacySceneRecorder.Record(mutatedInputs.World, mutatedInputs.Catalogs, mutatedInputs);
        // Source tiles are one metre, so this roof cell spans [0, 64] x [-64, 0].
        Assert.Contains(expected.Frames.Single(f => f.ProbeId == "AlwaysVisible40").Draws,
            d => d.Key is { OwnerId: "cluster/caps/0/-1/0", PartId: "hlod" });
        Assert.Equal(expected.Frames.Count, actual.Frames.Count);
        foreach (var pair in expected.Frames.Zip(actual.Frames))
        {
            Assert.Equal(pair.First.ProbeId, pair.Second.ProbeId);
            Assert.Equal(pair.First.Focus, pair.Second.Focus);
            Assert.Equal(pair.First.Draws.Select(Descriptor), pair.Second.Draws.Select(Descriptor));
            foreach (var draw in pair.First.Draws.Zip(pair.Second.Draws))
            {
                Assert.Equal(draw.First.Mesh.Vertices, draw.Second.Mesh.Vertices);
                Assert.Equal(draw.First.Mesh.Indices32, draw.Second.Mesh.Indices32);
            }
        }
        static object Descriptor(LegacySceneDraw draw) => new
        { draw.Key, draw.World, draw.ShadowOnly, draw.Dissolve, draw.Complement, draw.InvertShadow, draw.CastShadows, draw.Maps };
    }

    [Fact]
    public void SceneFullPath_RejectsNativeHlodTileSizeMutation()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("roof-hlod-native-tile-size");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => ShippedSceneComparison.Compare(inputs, report)));
        Assert.Contains("scene-roof-channel", fixture.FailureCategories());
    }
}

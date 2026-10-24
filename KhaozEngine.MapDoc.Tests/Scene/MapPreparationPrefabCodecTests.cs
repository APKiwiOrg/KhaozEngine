using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationPrefabCodecTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedPrefabDigest_ReleasesConsumedPatchEncoding(bool record)
    {
        var patch = new MapSurfacePatch
        {
            Key = new("invented", 0, 0),
            Width = 1,
            Depth = 1,
            Heights = new int[4],
            Cells = new MapSurfaceCell[1],
            Presence = [1UL],
        };
        MapPrefabChild child = record
            ? new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "wall"),
                new MapWallStrip("wall", new("lower", patch.Key), new("upper", patch.Key), MapStripFacing.TwoSided, 1), patch.Key)
            : new MapPrefabSurfaceChild(new(MapPrefabChildKind.Surface, "invented"),
                Prefabs.PrefabFixtures.Surface("invented", MapSurfaceRole.SupportFloor), [patch]);
        string expected = MapPrefabIdentity.ChildDigest(child);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 32_768));
        using var active = admission.Enter();
        using var operation = admission.BeginPreparation();
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(expected, MapPrefabIdentity.ChildDigest(child));
            Assert.Equal(0, admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
        }
    }
}

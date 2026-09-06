using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.TileWorld;

public sealed record TileFoliageDrawRecord(
    IReadOnlyList<GroundCoverInstance> Instances,
    Vector3 Focus,
    GroundCoverRenderOptions Options,
    int Drawn);

public sealed partial class RecordingTileWorldScene
{
    public List<TileFoliageDrawRecord> FoliageDraws { get; } = new();
    public List<IReadOnlyList<GroundCoverInstance>> FoliageReleases { get; } = new();

    public void ReleaseGroundCover(IReadOnlyList<GroundCoverInstance> cover) => FoliageReleases.Add(cover);

    public int DrawGroundCover(
        IReadOnlyList<GroundCoverInstance> cover,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
        Vector3 focus,
        GroundCoverRenderOptions options)
    {
        var queue = new SceneInstances();
        int drawn = GroundCoverRenderer.Queue(queue, cover, parts, focus, options);
        FoliageDraws.Add(new TileFoliageDrawRecord(cover, focus, options, drawn));
        return drawn;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
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
        var snapshot = new GroundCoverRenderOptions
        {
            DrawRadius = options.DrawRadius,
            DensityRadius = options.DensityRadius,
            FadeBandWidth = options.FadeBandWidth,
            QualityDensity = options.QualityDensity,
            DistantDensity = options.DistantDensity,
            CastsShadows = options.CastsShadows,
            MotionProfiles = new Dictionary<string, FoliageMotionProfile>(options.MotionProfiles, StringComparer.Ordinal),
            ModelCastsShadows = new Dictionary<string, bool>(options.ModelCastsShadows, StringComparer.Ordinal),
            InstanceFadeBandWidth = options.InstanceFadeBandWidth,
            FadeMode = options.FadeMode,
            UseGpuBatches = options.UseGpuBatches,
            WindDirection = options.WindDirection,
            WindStrength = options.WindStrength,
            WindSpeed = options.WindSpeed,
            WindSpatialFrequency = options.WindSpatialFrequency,
            WindFadeBladePixels = options.WindFadeBladePixels,
            Interactors = options.Interactors.ToArray(),
        };
        FoliageDraws.Add(new TileFoliageDrawRecord(cover, focus, snapshot, drawn));
        return drawn;
    }
}

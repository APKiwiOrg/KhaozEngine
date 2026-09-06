using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

internal sealed class GroundCoverGpuCache
{
    readonly Dictionary<GroundCoverBatch, Entry> _entries = new(ReferenceEqualityComparer.Instance);

    public int Draw(Scene3D scene, GroundCoverBatch cover,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes,
        Vector3 focus, GroundCoverRenderOptions options)
    {
        Span<FoliageInteractor> interactors = stackalloc FoliageInteractor[4];
        FoliageRenderSettings settings = ReadOptions(options, focus, interactors, out int count);
        if (settings.QualityDensity == 0f) return 0;
        if (!_entries.TryGetValue(cover, out Entry? entry))
        {
            entry = new Entry(cover);
            _entries.Add(cover, entry);
        }
        bool changed = entry.Bindings.Refresh(meshes);
        FoliageBatch? batch = entry.Batch;
        if (changed || batch is null || batch.IsDisposed)
        {
            entry.Batch = null;
            batch?.Dispose();
            batch = scene.CreateFoliageBatch(entry.Bindings.Expand());
            entry.Batch = batch;
        }
        return scene.DrawFoliage(batch, focus, settings, interactors[..count]);
    }

    public void Release(GroundCoverBatch cover)
    {
        if (_entries.Remove(cover, out Entry? entry)) entry.Batch?.Dispose();
    }

    internal static bool CanRetain(IReadOnlyList<GroundCoverInstance> cover, GroundCoverRenderOptions options) =>
        options.UseGpuBatches && cover is GroundCoverBatch &&
        options.FadeMode == GroundCoverFadeMode.HeightScale && !options.CastsShadows;

    internal static FoliageRenderSettings ReadOptions(GroundCoverRenderOptions options, Vector3 focus,
        Span<FoliageInteractor> interactors, out int count)
    {
        if (!float.IsFinite(focus.X) || !float.IsFinite(focus.Y) || !float.IsFinite(focus.Z))
            throw new ArgumentException("Ground cover focus must be finite.", nameof(focus));
        var settings = new FoliageRenderSettings
        {
            DrawRadius = options.DrawRadius,
            DensityRadius = options.DensityRadius,
            FadeBandWidth = options.FadeBandWidth,
            InstanceFadeBandWidth = options.InstanceFadeBandWidth,
            QualityDensity = options.QualityDensity,
            DistantDensity = options.DistantDensity,
            WindDirection = options.WindDirection,
            WindStrength = options.WindStrength,
            WindSpeed = options.WindSpeed,
            WindSpatialFrequency = options.WindSpatialFrequency,
        };
        settings.Validate();
        ArgumentNullException.ThrowIfNull(options.Interactors);
        count = options.Interactors.Count;
        if (count > 4)
            throw new ArgumentException("Ground cover accepts at most four interactors.", nameof(options));
        for (int i = 0; i < count; i++)
        {
            FoliageInteractor interactor = options.Interactors[i];
            interactor.Validate();
            interactors[i] = interactor;
        }
        return settings;
    }

    sealed class Entry(GroundCoverBatch cover)
    {
        public GroundCoverGpuBindings Bindings { get; } = new(cover);
        public FoliageBatch? Batch { get; set; }
    }
}

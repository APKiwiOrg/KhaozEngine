using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    readonly Dictionary<RegionHandles, AnimatedPropSplit?[]> _animatedPropSplits = new();
    AnimatedFoliageStyleSnapshot _animatedFoliageStyles;
    readonly GroundCoverRenderOptions _animatedFoliageOptions = new()
    {
        UseGpuBatches = true,
        FadeMode = GroundCoverFadeMode.HeightScale,
        QualityDensity = 1f,
        DistantDensity = 1f,
        CastsShadows = false,
    };

    void ValidateAnimatedFoliageStyles()
    {
        if (!_animatedFoliageStyles.Matches(_options.AnimatedFoliageArchetypes, _options.AnimatedFoliageStyles))
            _animatedFoliageStyles = new AnimatedFoliageStyleSnapshot(_options.AnimatedFoliageArchetypes,
                _options.AnimatedFoliageStyles, _catalogs, _propMeshes);
    }

    int DrawGroundProps(RegionHandles handles, int plane, Vector3 focus, ref int shadowOnly)
    {
        TileRegionProps props = handles.Props[plane];
        IReadOnlyList<PropPlacement> ground = props.Ground;
        IReadOnlySet<string> selection = _options.AnimatedFoliageArchetypes;
        _animatedPropSplits.TryGetValue(handles, out AnimatedPropSplit?[]? splits);
        AnimatedPropSplit? split = splits?[plane];
        // Builds and archetype overrides replace their ground list. A slot owns its previous split until
        // replacement is observed here or the region is freed, so discarded lists never accumulate.
        // The global snapshot is validated in Draw. A split compares only the archetypes its ground list draws.
        if (split is null || !ReferenceEquals(split.Source, ground) || !split.Key.Matches(_animatedFoliageStyles))
        {
            AnimatedPropSplit? replacement = selection.Count == 0 || ground.Count == 0
                ? null : new AnimatedPropSplit(ground, props.GroundObjectIds, _animatedFoliageStyles);
            if (split is not null) ReleaseAnimatedFoliage(handles, plane);
            split = replacement;
            if (split is not null)
            {
                if (splits is null)
                {
                    splits = new AnimatedPropSplit?[_planes];
                    _animatedPropSplits.Add(handles, splits);
                }
                splits[plane] = split;
            }
        }
        bool faded = HasCameraFadeOn(props.Region, plane);
        if (split is null) return DrawOrdinaryProps(ground, props.GroundObjectIds, faded, focus, ref shadowOnly);
        int drawn = DrawOrdinaryProps(split.Ordinary, split.OrdinaryObjectIds, faded, focus, ref shadowOnly);
        if (split.Foliage.Count == 0) return drawn;
        GroundCoverRenderOptions source = _options.GroundCover;
        _animatedFoliageOptions.DrawRadius = _options.PropDrawRadius;
        _animatedFoliageOptions.FadeBandWidth = source.FadeBandWidth;
        _animatedFoliageOptions.InstanceFadeBandWidth = source.InstanceFadeBandWidth;
        _animatedFoliageOptions.WindDirection = source.WindDirection;
        _animatedFoliageOptions.WindStrength = source.WindStrength;
        _animatedFoliageOptions.WindSpeed = source.WindSpeed;
        _animatedFoliageOptions.WindSpatialFrequency = source.WindSpatialFrequency;
        _animatedFoliageOptions.WindFadeBladePixels = source.WindFadeBladePixels;
        _animatedFoliageOptions.Interactors = source.Interactors;
        _animatedFoliageOptions.MotionProfiles = _animatedFoliageStyles.MotionProfiles;
        _animatedFoliageOptions.ModelCastsShadows = _animatedFoliageStyles.ModelCastsShadows;
        return drawn + _scene.DrawGroundCover(split.Foliage, _propMeshes, focus, _animatedFoliageOptions);
    }

    int DrawOrdinaryProps(IReadOnlyList<PropPlacement> placements, IReadOnlyList<long> objectIds, bool faded,
        Vector3 focus, ref int shadowOnly)
    {
        if (placements.Count == 0) return 0;
        if (faded) return DrawCameraFaded(placements, objectIds, focus, ref shadowOnly);
        return _scene.DrawProps(placements, _propMeshes, focus, _options.PropDrawRadius);
    }

    /// <summary>Whether an archetype draws through the animated ground-cover path rather than as a prop: the split
    /// below sends it there exactly when the view animates foliage and the archetype has a motion profile.</summary>
    internal bool DrawsAsAnimatedFoliage(string archetypeId) =>
        _options.AnimatedFoliageArchetypes.Count > 0 && _animatedFoliageStyles.MotionProfiles.ContainsKey(archetypeId);

    void ReleaseAnimatedFoliage(RegionHandles handles, int plane)
    {
        if (!_animatedPropSplits.TryGetValue(handles, out AnimatedPropSplit?[]? splits)) return;
        if (splits[plane] is { Foliage.Count: > 0 } split) _scene.ReleaseGroundCover(split.Foliage);
        splits[plane] = null;
    }

    void ReleaseAnimatedFoliage(RegionHandles handles)
    {
        if (!_animatedPropSplits.Remove(handles, out AnimatedPropSplit?[]? splits)) return;
        foreach (AnimatedPropSplit? split in splits)
            if (split is { Foliage.Count: > 0 }) _scene.ReleaseGroundCover(split.Foliage);
    }

    sealed class AnimatedPropSplit
    {
        public AnimatedFoliageSplitKey Key { get; }
        public IReadOnlyList<PropPlacement> Source { get; }
        public IReadOnlyList<PropPlacement> Ordinary { get; }
        public IReadOnlyList<long> OrdinaryObjectIds { get; }
        public GroundCoverBatch Foliage { get; }

        public AnimatedPropSplit(IReadOnlyList<PropPlacement> source, IReadOnlyList<long> sourceIds,
            AnimatedFoliageStyleSnapshot styles)
        {
            Key = new AnimatedFoliageSplitKey(source, styles);
            Source = source;
            var ordinary = new List<PropPlacement>();
            var ordinaryIds = new List<long>();
            var foliage = new List<GroundCoverInstance>();
            for (int i = 0; i < source.Count; i++)
            {
                PropPlacement prop = source[i];
                if (!styles.MotionProfiles.ContainsKey(prop.Id))
                {
                    ordinary.Add(prop);
                    // A hand-built list may carry no ids. Only the camera fade reads them, and it reads none then.
                    ordinaryIds.Add(i < sourceIds.Count ? sourceIds[i] : 0L);
                    continue;
                }
                var position = new Vector3(prop.X, prop.Y, prop.Z);
                Matrix4x4 transform = Matrix4x4.CreateScale(prop.Scale) *
                    Matrix4x4.CreateRotationY(prop.Yaw) * Matrix4x4.CreateTranslation(position);
                foliage.Add(new GroundCoverInstance(prop.Id, position, transform, 0f));
            }
            Ordinary = foliage.Count == 0 ? source : ordinary.ToArray();
            OrdinaryObjectIds = foliage.Count == 0 ? sourceIds : ordinaryIds.ToArray();
            Foliage = new GroundCoverBatch(foliage);
        }
    }
}

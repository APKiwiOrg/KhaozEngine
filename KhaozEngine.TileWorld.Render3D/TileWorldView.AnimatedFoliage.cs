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

    int DrawGroundProps(RegionHandles handles, int plane, Vector3 focus)
    {
        IReadOnlyList<PropPlacement> ground = handles.Props[plane].Ground;
        IReadOnlySet<string> selection = _options.AnimatedFoliageArchetypes;
        _animatedPropSplits.TryGetValue(handles, out AnimatedPropSplit?[]? splits);
        AnimatedPropSplit? split = splits?[plane];
        // Builds and archetype overrides replace their ground list. A slot owns its previous split until
        // replacement is observed here or the region is freed, so discarded lists never accumulate.
        if (split is null || !ReferenceEquals(split.Source, ground) ||
            !split.Matches(selection, _options.AnimatedFoliageStyles))
        {
            AnimatedPropSplit? replacement = selection.Count == 0 || ground.Count == 0
                ? null : new AnimatedPropSplit(ground, _animatedFoliageStyles);
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
        if (split is null) return DrawOrdinaryProps(ground, focus);
        int drawn = DrawOrdinaryProps(split.Ordinary, focus);
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
        _animatedFoliageOptions.MotionProfiles = split.Styles.MotionProfiles;
        _animatedFoliageOptions.ModelCastsShadows = split.Styles.ModelCastsShadows;
        return drawn + _scene.DrawGroundCover(split.Foliage, _propMeshes, focus, _animatedFoliageOptions);
    }

    int DrawOrdinaryProps(IReadOnlyList<PropPlacement> placements, Vector3 focus) => placements.Count == 0
        ? 0 : _scene.DrawProps(placements, _propMeshes, focus, _options.PropDrawRadius);

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
        public AnimatedFoliageStyleSnapshot Styles { get; }
        public IReadOnlyList<PropPlacement> Source { get; }
        public IReadOnlyList<PropPlacement> Ordinary { get; }
        public GroundCoverBatch Foliage { get; }

        public AnimatedPropSplit(IReadOnlyList<PropPlacement> source, AnimatedFoliageStyleSnapshot styles)
        {
            Styles = styles;
            Source = source;
            var ordinary = new List<PropPlacement>();
            var foliage = new List<GroundCoverInstance>();
            foreach (PropPlacement prop in source)
            {
                if (!styles.MotionProfiles.ContainsKey(prop.Id))
                {
                    ordinary.Add(prop);
                    continue;
                }
                var position = new Vector3(prop.X, prop.Y, prop.Z);
                Matrix4x4 transform = Matrix4x4.CreateScale(prop.Scale) *
                    Matrix4x4.CreateRotationY(prop.Yaw) * Matrix4x4.CreateTranslation(position);
                foliage.Add(new GroundCoverInstance(prop.Id, position, transform, 0f));
            }
            Ordinary = foliage.Count == 0 ? source : ordinary.ToArray();
            Foliage = new GroundCoverBatch(foliage);
        }

        public bool Matches(IReadOnlySet<string> selection, IReadOnlyDictionary<string, AnimatedFoliageStyle> styles) =>
            Styles.Matches(selection, styles);
    }
}

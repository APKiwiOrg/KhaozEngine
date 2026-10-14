using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

// A retained split depends only on the archetypes its ground list draws. Replacement compares their
// selection and effective style, so a change to any other archetype keeps the batch.
internal sealed class AnimatedFoliageSplitKey
{
    readonly string[] _ids;
    readonly bool[] _selected;
    readonly FoliageMotionProfile[] _motion;
    readonly bool[] _casts;
    AnimatedFoliageStyleSnapshot _matched;

    public AnimatedFoliageSplitKey(IReadOnlyList<PropPlacement> source, AnimatedFoliageStyleSnapshot styles)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(styles);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropPlacement prop in source) ids.Add(prop.Id);
        _ids = new string[ids.Count];
        ids.CopyTo(_ids);
        _selected = new bool[_ids.Length];
        _motion = new FoliageMotionProfile[_ids.Length];
        _casts = new bool[_ids.Length];
        for (int i = 0; i < _ids.Length; i++)
        {
            _selected[i] = styles.MotionProfiles.TryGetValue(_ids[i], out _motion[i]);
            if (_selected[i]) _casts[i] = styles.ModelCastsShadows[_ids[i]];
        }
        _matched = styles;
    }

    public bool Matches(AnimatedFoliageStyleSnapshot styles)
    {
        ArgumentNullException.ThrowIfNull(styles);
        if (ReferenceEquals(styles, _matched)) return true;
        for (int i = 0; i < _ids.Length; i++)
        {
            bool selected = styles.MotionProfiles.TryGetValue(_ids[i], out FoliageMotionProfile motion);
            if (selected != _selected[i]) return false;
            if (selected && (motion != _motion[i] || styles.ModelCastsShadows[_ids[i]] != _casts[i])) return false;
        }
        _matched = styles;
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

// A split retains detached settings. Catalog references and resolved multipart handles must agree
// on their effective style before any replacement maps become visible to the renderer.
internal sealed class AnimatedFoliageStyleSnapshot
{
    readonly HashSet<string> _selection;
    readonly Dictionary<string, AnimatedFoliageStyle> _styles;

    public IReadOnlyDictionary<string, FoliageMotionProfile> MotionProfiles { get; }
    public IReadOnlyDictionary<string, bool> ModelCastsShadows { get; }

    public AnimatedFoliageStyleSnapshot(IReadOnlySet<string> selection,
        IReadOnlyDictionary<string, AnimatedFoliageStyle> styles, TileWorldCatalogs catalogs,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(styles);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(meshes);
        _selection = new HashSet<string>(selection, StringComparer.Ordinal);
        _styles = new Dictionary<string, AnimatedFoliageStyle>(styles, StringComparer.Ordinal);

        var styleIds = new List<string>(_styles.Keys);
        styleIds.Sort(StringComparer.Ordinal);
        foreach (string id in styleIds)
            if (!_selection.Contains(id))
                throw new ArgumentException($"Animated foliage style '{id}' is outside the selection.",
                    nameof(TileWorldViewOptions.AnimatedFoliageStyles));

        var ids = new List<string>(_selection);
        ids.Sort(StringComparer.Ordinal);
        var meshRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        var partHandles = new Dictionary<MeshHandle, string>();
        var motion = new Dictionary<string, FoliageMotionProfile>(StringComparer.Ordinal);
        var casts = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            string? meshRef = catalogs.Archetype(id)?.MeshRef;
            if (!string.IsNullOrEmpty(meshRef)) ValidateSharing(meshRefs, meshRef, id);
            if (meshes.TryGetValue(id, out IReadOnlyList<MeshHandle>? parts))
                foreach (MeshHandle part in parts) ValidateSharing(partHandles, part, id);
            AnimatedFoliageStyle style = EffectiveStyle(id);
            motion.Add(id, style.Motion);
            casts.Add(id, style.CastsShadows);
        }
        MotionProfiles = new ReadOnlyDictionary<string, FoliageMotionProfile>(motion);
        ModelCastsShadows = new ReadOnlyDictionary<string, bool>(casts);
    }

    public bool Matches(IReadOnlySet<string> selection, IReadOnlyDictionary<string, AnimatedFoliageStyle> styles)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(styles);
        if (_selection.Count != selection.Count || _styles.Count != styles.Count) return false;
        foreach (string id in selection)
            if (!_selection.Contains(id)) return false;
        foreach (KeyValuePair<string, AnimatedFoliageStyle> entry in styles)
            if (!_styles.TryGetValue(entry.Key, out AnimatedFoliageStyle old) || old != entry.Value) return false;
        return true;
    }

    AnimatedFoliageStyle EffectiveStyle(string id) => _styles.TryGetValue(id, out AnimatedFoliageStyle style)
        ? style : new AnimatedFoliageStyle(FoliageMotionProfile.Blade, false);

    void ValidateSharing<TKey>(Dictionary<TKey, string> owners, TKey mesh, string id) where TKey : notnull
    {
        if (!owners.TryGetValue(mesh, out string? owner))
        {
            owners.Add(mesh, id);
            return;
        }
        if (EffectiveStyle(owner) != EffectiveStyle(id))
            throw new ArgumentException($"Animated foliage archetypes '{owner}' and '{id}' share a mesh but have conflicting styles.",
                nameof(TileWorldViewOptions.AnimatedFoliageStyles));
    }
}

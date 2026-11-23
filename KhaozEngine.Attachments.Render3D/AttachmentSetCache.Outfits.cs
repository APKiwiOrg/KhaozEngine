using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

public sealed partial class AttachmentSetCache
{
    readonly Dictionary<AttachmentMeshRef, BodyRegionMap> _bodyMaps = [];
    OutfitViewCache? _outfitViews;

    /// <summary>Load a verified body region map and check it against the uploaded body it tags.</summary>
    /// <remarks>Map part <c>i</c> tags <paramref name="bodyParts"/>[i], uploaded from <paramref name="bodyMeshes"/>[i],
    /// the body GLB's skinned material parts in order. The rig identity is computed from the body's GLB bytes, so the
    /// caller supplies it. A map for another rig, another part count or other triangle counts refuses with
    /// <see cref="AttachmentBindingRefusal.BodyRegionMapMismatch"/>. Malformed map bytes refuse with
    /// <see cref="BodyRegionMapDataException"/>. The returned view borrows this cache, and the body's handles stay the
    /// caller's.</remarks>
    public LoadedBodyRegions GetBodyRegions(string key, string expectedSha256, string bodyRigIdentity,
        IReadOnlyList<SkinnedMeshHandle> bodyParts, IReadOnlyList<SkinnedGltfMesh> bodyMeshes)
    {
        ThrowIfDisposed();
        ValidateRequest(key, expectedSha256);
        ArgumentNullException.ThrowIfNull(bodyRigIdentity);
        ArgumentNullException.ThrowIfNull(bodyParts);
        ArgumentNullException.ThrowIfNull(bodyMeshes);
        if (bodyMeshes.Count != bodyParts.Count)
            throw new ArgumentException("A body needs one mesh per part handle.", nameof(bodyMeshes));
        var reference = new AttachmentMeshRef(key, expectedSha256);
        if (!_bodyMaps.TryGetValue(reference, out BodyRegionMap? map))
        {
            map = BodyRegionMapJson.Parse(ReadVerified(reference, key), key);
            _bodyMaps.Add(reference, map);
        }
        if (!string.Equals(map.RigIdentity, bodyRigIdentity, StringComparison.Ordinal))
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.BodyRegionMapMismatch,
                $"{key}: the region map tags rig '{map.RigIdentity}', not the body's '{bodyRigIdentity}'.")
            { SourceName = key, ExpectedRigIdentity = bodyRigIdentity, ActualRigIdentity = map.RigIdentity };
        if (map.Parts.Count != bodyParts.Count)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.BodyRegionMapMismatch,
                $"{key}: the region map tags {map.Parts.Count} parts, the body has {bodyParts.Count}.")
            { SourceName = key, ExpectedPartCount = bodyParts.Count, ActualPartCount = map.Parts.Count };
        var indices = new uint[bodyParts.Count][];
        Skeleton? hostSkeleton = null;
        for (int part = 0; part < indices.Length; part++)
        {
            SkinnedGltfMesh mesh = bodyMeshes[part] ?? throw new ArgumentNullException(nameof(bodyMeshes), $"bodyMeshes[{part}] is null.");
            if (mesh.TriangleCount != map.TriangleCount(part))
                throw new AttachmentBindingDataException(AttachmentBindingRefusal.BodyRegionMapMismatch,
                    $"{key}: part {part} tags {map.TriangleCount(part)} triangles, the body's mesh has {mesh.TriangleCount}.")
                { SourceName = key, BodyPartIndex = part, ExpectedTriangleCount = mesh.TriangleCount, ActualTriangleCount = map.TriangleCount(part) };
            if (part == 0) hostSkeleton = mesh.Skeleton;
            indices[part] = (uint[])mesh.Indices32.Clone();
        }
        var handles = new SkinnedMeshHandle[bodyParts.Count];
        for (int part = 0; part < handles.Length; part++) handles[part] = bodyParts[part];
        return new LoadedBodyRegions(this, map, handles, indices, hostSkeleton);
    }

    /// <summary>Apply the outfit rule to <paramref name="body"/> and <paramref name="worn"/> and build every view the
    /// result needs. All checks run before the first view is created, and a refused view releases the ones before it.
    /// </summary>
    /// <remarks>A set with no layer covers nothing and nothing hides it. A refusal names the worn set by its cache key:
    /// <see cref="AttachmentBindingRefusal.OutfitViewsUnsupported"/> over a scene that is not an
    /// <see cref="IAttachmentViewScene"/>, <see cref="AttachmentBindingRefusal.OutfitLiftUnsupported"/> when a part
    /// needs a displaced view over a plain view scene, <see cref="AttachmentBindingRefusal.PartRegionTagsMismatch"/> for a skinned
    /// part whose runs miss its triangles, and <see cref="AttachmentBindingRefusal.OutfitRegionUnknown"/> for a region
    /// the body lacks. A footprint for this body whose part triangle count differs from its mesh refuses with
    /// <see cref="AttachmentBindingRefusal.FootprintTriangleCountMismatch"/>. Dispose the outfit before this cache
    /// and before the body's meshes unload.</remarks>
    public BoundOutfit BindOutfit(LoadedBodyRegions body, IReadOnlyList<BoundAttachmentSet> worn)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(worn);
        if (!ReferenceEquals(body.Owner, this))
            throw new ArgumentException("An outfit needs body regions loaded by this cache.", nameof(body));
        var sets = new BoundAttachmentSet[worn.Count];
        var definitions = new AttachmentSetDefinition[sets.Length];
        for (int index = 0; index < sets.Length; index++)
        {
            sets[index] = worn[index] ?? throw new ArgumentNullException(nameof(worn), $"worn[{index}] is null.");
            if (!ReferenceEquals(sets[index].Owner, this))
                throw new ArgumentException($"worn[{index}] is bound through another cache.", nameof(worn));
            definitions[index] = sets[index].Definition;
        }
        if (Scene is not IAttachmentViewScene viewScene)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.OutfitViewsUnsupported,
                "Drawing an outfit needs a scene that creates skinned mesh views, an IAttachmentViewScene.");
        HashSet<AttachmentMeshRef>? liftingMeshes = LiftingMeshReferences(definitions);
        var read = new Dictionary<MeshIdentity, OutfitMeshGeometry>();
        var tagged = new ReadOnlyMemory<uint>[]?[sets.Length][];
        for (int index = 0; index < sets.Length; index++) tagged[index] = TaggedIndices(sets[index], liftingMeshes, read);
        OutfitCoverage coverage = ResolveOutfit(body.Map, sets, definitions);
        LiftFootprint?[]? footprints = ReadOutfitFootprints(body, sets, tagged);
        OutfitLift? lift = ResolveOutfitLift(coverage, sets, read, footprints);
        bool[]? footprintApplied = footprints is null ? null : FootprintApplications(lift!, sets.Length);

        var pending = new List<(int Worn, int Slot, ViewKey Key)>();
        var draws = new OutfitDraw[sets.Length][];
        for (int index = 0; index < sets.Length; index++)
            draws[index] = PlanWorn(sets[index], tagged[index], coverage, lift, index, pending);
        int wornViews = pending.Count;
        var bodyMeshes = new SkinnedMeshHandle[body.Parts.Count];
        var bodyVisible = new bool[bodyMeshes.Length];
        for (int part = 0; part < bodyMeshes.Length; part++)
        {
            var cursor = new RunCursor();
            uint[]? visible = Visible(body.Indices[part], body.Map.Parts[part], ref cursor, coverage.BodyHidden);
            bodyVisible[part] = visible is not { Length: 0 };
            if (visible is null) bodyMeshes[part] = body.Parts[part];
            else if (visible.Length > 0) pending.Add((-1, part, new ViewKey(body.Parts[part], visible)));
        }

        OutfitViewCache views = _outfitViews ??= new OutfitViewCache(viewScene);
        var used = new ViewKey[pending.Count];
        int acquired = 0;
        try
        {
            for (; acquired < used.Length; acquired++)
            {
                (int index, int slot, ViewKey key) = pending[acquired];
                SkinnedMeshHandle view = views.Acquire(key, out used[acquired]);
                if (acquired < wornViews) draws[index][slot] = draws[index][slot] with { Skinned = view };
                else bodyMeshes[slot] = view;
            }
        }
        catch
        {
            // Give back what this bind took. The refusal that stopped it is the one the caller needs, so a failed
            // release here is not allowed to replace it.
            for (int i = 0; i < acquired; i++)
            {
                try { views.Release(used[i]); }
                catch (Exception) { }
            }
            throw;
        }
        return new BoundOutfit(this, views, sets, draws, bodyMeshes, bodyVisible, used, body, footprintApplied);
    }

    // The index lists of every skinned part that carries region runs, read when an outfit is bound so a cache that
    // never binds one keeps nothing extra. A part's runs cover its triangles in material order, then each material
    // handle's triangle order, and must add up to them. Parts without runs, and rigid parts, get null.
    ReadOnlyMemory<uint>[]?[] TaggedIndices(BoundAttachmentSet set, HashSet<AttachmentMeshRef>? liftingMeshes,
        Dictionary<MeshIdentity, OutfitMeshGeometry> read)
    {
        var tagged = new ReadOnlyMemory<uint>[]?[set.Definition.Parts.Count];
        for (int part = 0; part < tagged.Length; part++)
        {
            if (set.Definition.Parts[part] is not SkinnedAttachmentPartDefinition { Regions.Count: > 0 } skinned) continue;
            bool needsVertices = liftingMeshes?.Contains(skinned.Mesh) == true;
            ReadOnlyMemory<uint>[] indices = ReadOutfitGeometry(skinned.Mesh, needsVertices, read).Indices;
            int runs = 0;
            foreach (NamedRegionRun run in skinned.Regions) runs += run.TriangleCount;
            int triangles = TriangleCount(indices);
            if (runs != triangles)
                throw new AttachmentBindingDataException(AttachmentBindingRefusal.PartRegionTagsMismatch,
                    $"{set.SourceName}: part {skinned.Id} tags {runs} triangles, its mesh '{skinned.Mesh.Key}' has {triangles}.")
                {
                    SourceName = set.SourceName, PartId = skinned.Id, MeshKey = skinned.Mesh.Key,
                    ExpectedTriangleCount = triangles, ActualTriangleCount = runs,
                };
            tagged[part] = indices;
        }
        return tagged;
    }

    static OutfitCoverage ResolveOutfit(BodyRegionMap map, BoundAttachmentSet[] sets, AttachmentSetDefinition[] definitions)
    {
        try { return OutfitCoverage.Resolve(map, definitions); }
        catch (OutfitDataException error) when (error.Reason == OutfitRefusal.RegionUnknown)
        {
            string? source = error.WornIndex is int index ? sets[index].SourceName : null;
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.OutfitRegionUnknown,
                $"{source}: the body has no region '{error.RegionName}'.", error)
            {
                SourceName = source, WornIndex = error.WornIndex, PartId = error.PartId,
                RunIndex = error.RunIndex, RegionName = error.RegionName,
            };
        }
    }

    // Walks the set's draws in BoundAttachmentSet's order, one per material group of every part, and keeps those the
    // outfit leaves visible. A partly covered or lifted material group gets its view once every check has passed.
    OutfitDraw[] PlanWorn(BoundAttachmentSet set, ReadOnlyMemory<uint>[]?[] tagged, OutfitCoverage coverage,
        OutfitLift? lift, int wornIndex, List<(int Worn, int Slot, ViewKey Key)> pending)
    {
        var kept = new List<OutfitDraw>();
        RegionSet hidden = coverage.HiddenFor(wornIndex);
        int draw = 0;
        for (int part = 0; part < set.Resources.Count; part++)
        {
            AttachmentPartResources resources = set.Resources[part];
            uint partId = set.Definition.Parts[part].Id;
            if (!resources.IsSkinned)
            {
                bool covered = coverage.RigidPartHidden(wornIndex, partId);
                for (int material = 0; material < resources.RigidHandles.Count; material++, draw++)
                    if (!covered) kept.Add(new OutfitDraw(draw, part, false, resources.RigidHandles[material], default));
                continue;
            }
            IReadOnlyList<RegionRun> runs = coverage.SkinnedPartRegions(wornIndex, partId);
            IReadOnlyList<ReadOnlyMemory<Vector3>>? lifted = lift?.LiftedPositions(wornIndex, partId);
            var cursor = new RunCursor();
            for (int material = 0; material < resources.SkinnedHandles.Count; material++, draw++)
            {
                SkinnedMeshHandle source = resources.SkinnedHandles[material];
                uint[]? visible = runs.Count == 0 ? null : Visible(tagged[part]![material].Span, runs, ref cursor, hidden);
                if (visible is { Length: 0 }) continue;
                if (lifted is not null)
                    pending.Add((wornIndex, kept.Count, DisplacedViewKey(set, wornIndex, partId, source,
                        visible is null ? tagged[part]![material] : visible, lifted[material])));
                else if (visible is not null)
                    pending.Add((wornIndex, kept.Count, new ViewKey(source, visible)));
                kept.Add(new OutfitDraw(draw, part, true, default, source));
            }
        }
        return kept.ToArray();
    }

    /// <summary>The indices of the triangles whose region <paramref name="hidden"/> does not contain, null when that
    /// is every triangle and empty when it is none. <paramref name="cursor"/> carries the run position from one
    /// material handle of a part to the next.</summary>
    static uint[]? Visible(ReadOnlySpan<uint> indices, IReadOnlyList<RegionRun> runs, ref RunCursor cursor, RegionSet hidden)
    {
        if (hidden.IsEmpty) return null;
        int triangles = indices.Length / 3;
        var kept = new List<uint>(indices.Length);
        for (int triangle = 0; triangle < triangles; triangle++)
        {
            if (hidden.Contains(cursor.Next(runs))) continue;
            kept.Add(indices[3 * triangle]);
            kept.Add(indices[3 * triangle + 1]);
            kept.Add(indices[3 * triangle + 2]);
        }
        return kept.Count == 3 * triangles ? null : kept.ToArray();
    }

    struct RunCursor
    {
        int _next, _left, _region;

        // Runs are known to cover every triangle, so the cursor never passes the last run.
        internal int Next(IReadOnlyList<RegionRun> runs)
        {
            while (_left == 0)
            {
                RegionRun run = runs[_next++];
                _left = run.TriangleCount;
                _region = run.Region;
            }
            _left--;
            return _region;
        }
    }
}

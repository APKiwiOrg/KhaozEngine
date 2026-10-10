using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>One placement's native mesh top and collider extent, in asset-local metres: source units applied, placement
/// transform not applied. <see cref="RawMeshMaxY"/> is the highest mesh vertex as <see cref="NativeMapAssetLoader"/>
/// loads it, with no height normalization. <see cref="EffectiveBottom"/> and <see cref="EffectiveTop"/> are the
/// collider's vertical extent under the physics seam's conventions: boxes centred, cylinders base-aligned.</summary>
public sealed record NativeCollisionMeasurement(string PlacementId, string AssetId, float RawMeshMaxY,
    float EffectiveBottom, float EffectiveTop, string ColliderSha256);

/// <summary>The outcome of one collider height edit. <see cref="BeforeSha256"/> and <see cref="AfterSha256"/> are the
/// collider resource digests. <see cref="AffectedPlacementIds"/> lists, in ordinal order, every placement of the asset.
/// <see cref="Applied"/> is false for a dry run and for an edit that leaves the collider bytes unchanged.</summary>
public sealed record NativeCollisionEditResult(bool Applied, string BeforeSha256, string AfterSha256,
    IReadOnlyList<string> AffectedPlacementIds, MapNativeEditEffects Effects);

/// <summary>Measures and edits the colliders of the session's open native document. A height edit rewrites only the
/// asset's collider resource and the root manifest that declares it. The mesh resource and every placement transform
/// stay as they are.</summary>
public sealed class NativeCollisionService(MapEditSession session)
{
    /// <summary>What a height edit invalidates: every placement of the asset changes its physics, navigation and
    /// residency bounds. The old and new bounds are the placements' collider and interaction envelope bounds from
    /// <see cref="NativePlacementBoundsProvider"/>, as placement edits report them.</summary>
    public const MapNativeInvalidation HeightEditInvalidation =
        MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency;

    // The largest horizontal component a compound child's rotated local Y may keep. Any real tilt adds vertical extent
    // from the box's other axes, which would break the height round trip, so only float rounding is tolerated.
    const float VerticalAxisTolerance = 1e-6f;

    /// <summary>Measures the asset placed as <paramref name="placementId"/>. Throws <see cref="MapDocumentException"/>
    /// for an unknown placement, an asset without a collider, and an unreadable collider or mesh.</summary>
    public NativeCollisionMeasurement Measure(string placementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placementId);
        return session.WithNativeAssets(context =>
        {
            MapPlacement placement = context.Document.Placements.Find(p => string.Equals(p.Id, placementId, StringComparison.Ordinal))
                ?? throw new MapDocumentException($"No placement '{placementId}' in the open native document.");
            string assetId = placement.AssetId!;
            MapAssetShapes shapes = MapAssetShapes.Read(context.Assets, assetId);
            if (shapes.Collider is null) throw new MapDocumentException($"Asset '{assetId}' declares no collider.");
            MapBox3 extent = MapShapeBounds.Of(shapes.Collider, Pose.Identity);
            GltfMesh mesh = NativeMapAssetLoader.Load(context.Assets.GetAsset(assetId), context.Assets);
            float meshTop = float.NegativeInfinity;
            foreach (ModelVertex vertex in mesh.Vertices) meshTop = MathF.Max(meshTop, vertex.Position.Y);
            float units = shapes.SourceUnitsToMetres;
            return new NativeCollisionMeasurement(placementId, assetId, meshTop,
                (float)(extent.MinY * units), (float)(extent.MaxY * units), shapes.ColliderSha256!);
        });
    }

    /// <summary>Resizes the collider of <paramref name="assetId"/> so it spans <paramref name="bottom"/> to
    /// <paramref name="top"/> in asset-local metres. The collider must be a box or a compound of boxes whose orientations
    /// keep each box's local Y vertical, and anything else refuses with "compound boxes". Each box keeps its X and Z, and
    /// its centre and half height map linearly from the collider's current vertical extent onto the requested one. A
    /// lone box becomes a one-child compound, since a centred box cannot sit off its origin.
    /// <para>A dry run computes the new collider bytes, digest and effects and touches no file and no session state.
    /// Apply writes the collider and a new root manifest through <see cref="MapAssetFileWriter"/> under the document's
    /// resource root, then swaps that root reference in one native transaction whose write set sets
    /// <see cref="MapNativeWriteSet.NativeAssets"/>, binds the new closure and marks the session dirty. Save persists
    /// the document. The asset and its collider must be declared in one root manifest, and no other asset may share the
    /// collider resource. Throws <see cref="MapDocumentException"/> on every refusal.</para></summary>
    public NativeCollisionEditResult SetHeights(string assetId, float bottom, float top, bool dryRun = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        if (!float.IsFinite(bottom) || !float.IsFinite(top) || top <= bottom)
            throw new MapDocumentException($"Collider heights for '{assetId}' need a finite bottom below a finite top.");
        return session.WithNativeAssets(context =>
        {
            HeightPlan plan = PlanHeights(context, assetId, bottom, top);
            if (dryRun || plan.Unchanged)
                return new NativeCollisionEditResult(false, plan.BeforeSha256, plan.AfterSha256, plan.AffectedPlacementIds, plan.Effects);

            var writer = new MapAssetFileWriter(context.ResourceRoot);
            MapAssetRef collider = writer.WriteResource(plan.ColliderBytes, ColliderExtension) with { Id = plan.ColliderResourceId };
            MapAssetRef root = writer.WriteManifest(plan.Manifest, plan.NewRoot.Id);
            if (collider != plan.ColliderReference || root != plan.NewRoot)
                throw new MapDocumentException($"Collider edit for '{assetId}' wrote resources that differ from its plan.");
            MapAssetClosure assets = MapAssetClosure.Load(plan.NewRoots, context.Source);
            var command = new NativeAssetRootsCommand($"Set collider heights for {assetId}", plan.NewRoots, plan.Effects);
            MapNativeEditEffects effects = session.ApplyNativeAssets(command, assets);
            return new NativeCollisionEditResult(true, plan.BeforeSha256, plan.AfterSha256, plan.AffectedPlacementIds, effects);
        });
    }

    const string ColliderExtension = "coll";

    sealed record HeightPlan(string BeforeSha256, string AfterSha256, IReadOnlyList<string> AffectedPlacementIds,
        MapNativeEditEffects Effects, bool Unchanged, string ColliderResourceId, byte[] ColliderBytes,
        MapAssetRef ColliderReference, MapAssetManifestDoc Manifest, IReadOnlyList<MapAssetRef> NewRoots,
        MapAssetRef NewRoot);

    static HeightPlan PlanHeights(NativeAssetContext context, string assetId, float bottom, float top)
    {
        MapAssetClosure closure = context.Assets;
        MapResolvedAsset asset = closure.GetAsset(assetId);
        string colliderId = asset.CollisionResourceId
            ?? throw new MapDocumentException($"Asset '{assetId}' declares no collider.");
        MapResolvedAsset? sharer = closure.Assets.FirstOrDefault(a => a.Id != assetId && a.CollisionResourceId == colliderId);
        if (sharer is not null)
            throw new MapDocumentException(
                $"Asset '{assetId}' shares collider resource '{colliderId}' with asset '{sharer.Id}', so a height edit would change both.");
        MapAssetShapes shapes = MapAssetShapes.Read(closure, assetId);
        float units = shapes.SourceUnitsToMetres;
        PhysicsShape resized = ResizeVertically(shapes.Collider!, bottom / units, top / units, assetId);
        byte[] bytes = Serialize(resized);
        string before = shapes.ColliderSha256!;
        string after = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string[] affected = context.Document.Placements.Where(p => string.Equals(p.AssetId, assetId, StringComparison.Ordinal))
            .Select(p => p.Id).Order(StringComparer.Ordinal).ToArray();
        IReadOnlyList<MapAssetRef> oldRoots = context.Document.NativeAssets.ToArray();

        (int rootIndex, MapAssetManifestDoc manifest, MapResourceDoc resource) = FindDeclaringRoot(closure, oldRoots, assetId, colliderId);
        MapAssetRef colliderReference = MapAssetFileWriter.Address(bytes, ColliderExtension) with { Id = colliderId };
        resource.Reference = colliderReference;
        byte[] manifestBytes = MapAssetFileWriter.Serialize(manifest, oldRoots[rootIndex].Id);
        MapAssetRef newRoot = MapAssetFileWriter.Address(manifestBytes, MapAssetFileWriter.ManifestExtension) with { Id = oldRoots[rootIndex].Id };
        MapAssetRef[] newRoots = oldRoots.ToArray();
        newRoots[rootIndex] = newRoot;

        if (StringComparer.Ordinal.Equals(before, after))
        {
            var none = new MapNativeEditEffects(null, null, Array.Empty<MapPatchKey>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<MapDigestChange>(), MapNativeInvalidation.None);
            return new HeightPlan(before, after, Array.AsReadOnly(affected), none, true, colliderId, bytes, colliderReference, manifest,
                oldRoots, oldRoots[rootIndex]);
        }

        // Resolve the edited document over an in-memory overlay of the two new resources, so a dry run reads the same
        // closure an apply will write.
        var overlay = new OverlaySource(context.Source, (colliderReference.Path, bytes), (newRoot.Path, manifestBytes));
        MapAssetClosure edited = MapAssetClosure.Load(newRoots, overlay);
        MapDocument candidate = NativeDocumentSnapshot.Clone(context.Document, context.Registry);
        candidate.NativeAssets = newRoots.ToList();
        var digests = new List<MapDigestChange>
        {
            new($"asset/{assetId}/collider", before, after),
            new($"root/{newRoot.Id}", oldRoots[rootIndex].Sha256, newRoot.Sha256),
        };
        var effects = new MapNativeEditEffects(
            NativePlacementBoundsProvider.Instance.Bounds(context.Document, closure, context.Registry, affected),
            NativePlacementBoundsProvider.Instance.Bounds(candidate, edited, context.Registry, affected),
            Array.Empty<MapPatchKey>(), Array.Empty<string>(), Array.AsReadOnly(affected), digests.AsReadOnly(),
            HeightEditInvalidation);
        return new HeightPlan(before, after, Array.AsReadOnly(affected), effects, false, colliderId, bytes,
            colliderReference, manifest, Array.AsReadOnly(newRoots), newRoot);
    }

    // The root manifest that declares both the asset and its collider resource, freshly parsed so it can be edited.
    static (int Index, MapAssetManifestDoc Manifest, MapResourceDoc Resource) FindDeclaringRoot(MapAssetClosure closure,
        IReadOnlyList<MapAssetRef> roots, string assetId, string colliderId)
    {
        for (int i = 0; i < roots.Count; i++)
        {
            MapAssetManifestDoc manifest = MapAssetManifestReader.Read(closure.GetResource(roots[i].Id).Bytes.ToArray(), roots[i].Id);
            if (!manifest.Assets.Any(a => string.Equals(a.Id, assetId, StringComparison.Ordinal))) continue;
            MapResourceDoc resource = manifest.Resources.Find(r => string.Equals(r.Reference.Id, colliderId, StringComparison.Ordinal))
                ?? throw new MapDocumentException(
                    $"Asset '{assetId}' collider resource '{colliderId}' is not declared in root manifest '{roots[i].Id}', so it cannot be rewritten there.");
            return (i, manifest, resource);
        }
        throw new MapDocumentException(
            $"Asset '{assetId}' is not declared directly in a root manifest. Collider edits rewrite root manifests only.");
    }

    static PhysicsShape ResizeVertically(PhysicsShape collider, float bottom, float top, string assetId)
    {
        CompoundChild[] children = collider switch
        {
            BoxShape box => new[] { new CompoundChild(box, Pose.Identity) },
            CompoundShape compound when compound.Children.All(c => c.Shape is BoxShape) => compound.Children,
            _ => throw new MapDocumentException(
                $"Asset '{assetId}' collider is a {Describe(collider)}. Height edits apply only to boxes and compound boxes."),
        };
        foreach (CompoundChild child in children)
        {
            Vector3 up = Vector3.Transform(Vector3.UnitY, Quaternion.Normalize(child.Local.Orientation));
            if (MathF.Abs(up.X) > VerticalAxisTolerance || MathF.Abs(up.Z) > VerticalAxisTolerance)
                throw new MapDocumentException(
                    $"Asset '{assetId}' has a tilted box. Height edits apply only to boxes and compound boxes whose boxes stand upright.");
        }

        float low = float.PositiveInfinity, high = float.NegativeInfinity;
        foreach (CompoundChild child in children)
        {
            float half = ((BoxShape)child.Shape).HalfExtents.Y;
            low = MathF.Min(low, child.Local.Position.Y - half);
            high = MathF.Max(high, child.Local.Position.Y + half);
        }
        float scale = (top - bottom) / (high - low);
        var resized = new CompoundChild[children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            CompoundChild child = children[i];
            Vector3 half = ((BoxShape)child.Shape).HalfExtents;
            Vector3 position = child.Local.Position;
            var box = new BoxShape(new Vector3(half.X, half.Y * scale, half.Z));
            var centre = new Vector3(position.X, bottom + (position.Y - low) * scale, position.Z);
            resized[i] = new CompoundChild(box, new Pose(centre, child.Local.Orientation));
        }
        foreach (CompoundChild child in resized)
            if (!float.IsFinite(child.Local.Position.Y) || ((BoxShape)child.Shape).HalfExtents.Y is not (> 0f and < float.PositiveInfinity))
                throw new MapDocumentException($"Collider heights for '{assetId}' leave a box without a finite positive height.");
        return new CompoundShape(resized);
    }

    static string Describe(PhysicsShape shape) => shape switch
    {
        CompoundShape compound => $"compound holding a {compound.Children.First(c => c.Shape is not BoxShape).Shape.GetType().Name}",
        _ => shape.GetType().Name,
    };

    static byte[] Serialize(PhysicsShape shape)
    {
        using var stream = new MemoryStream();
        PropCollisionFormat.Write(shape, stream);
        return stream.ToArray();
    }

    // Serves the planned resources from memory by path and every other reference from the session's source.
    sealed class OverlaySource(IMapAssetSource inner, params (string Path, byte[] Bytes)[] planned) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference)
        {
            foreach ((string path, byte[] bytes) in planned)
                if (string.Equals(path, reference.Path, StringComparison.Ordinal)) return bytes;
            return inner.Read(reference);
        }
    }

    // Swaps the document's native asset roots in one native transaction. The tool keeps no undo history, so the command
    // only ever publishes forward through MapEditSession.ApplyNativeAssets.
    sealed class NativeAssetRootsCommand(string label, IReadOnlyList<MapAssetRef> roots, MapNativeEditEffects effects)
        : IEditorCommand, INativeDocumentCommand
    {
        const string ForwardOnly = "A native asset root swap publishes forward through MapEditSession.ApplyNativeAssets only.";

        public string Label => label;

        public void Apply(MapDocument doc) => throw new NotSupportedException(ForwardOnly);

        public void Revert(MapDocument doc) => throw new NotSupportedException(ForwardOnly);

        public bool TryMerge(IEditorCommand next) => false;

        public NativeDocumentPreparation Prepare(MapDocument candidate, bool undo)
        {
            if (undo) throw new NotSupportedException(ForwardOnly);
            candidate.NativeAssets = roots.ToList();
            var writes = new MapNativeWriteSet(Array.Empty<MapPatchKey>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<MapCornerOwnerChange>(), Array.Empty<ushort>(), false)
            { NativeAssets = true };
            return new NativeDocumentPreparation(writes, effects, () => { });
        }
    }
}

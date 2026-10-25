using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>One resolved placement's physical and interaction geometry in metres. The collider is scaled once by the
/// asset's source units times the placement scale and placed at <see cref="WorldPose"/>.</summary>
public sealed class MapPlacementGeometry
{
    /// <summary>The placement's stable editor identity.</summary>
    public string PlacementId { get; }

    /// <summary>The placement's optional game numeric identity.</summary>
    public long? NumericId { get; }

    /// <summary>The native asset the placement instances.</summary>
    public string AssetId { get; }

    /// <summary>The scaled blocking shape, or null when the asset is not solid.</summary>
    public PhysicsShape? Collider { get; }

    /// <summary>The placement position with its yaw about world Y. The orientation is (0, sin, 0, cos) of half the yaw,
    /// each taken in double and rounded to float once.</summary>
    public Pose WorldPose { get; }

    /// <summary>Conservative world bounds of <see cref="Collider"/> across all permitted registration origins,
    /// including the mathematical pose, or null without a collider.</summary>
    public MapBox3? ColliderBounds { get; }

    /// <summary>The interaction envelope under <see cref="MapInteractionPolicy"/>.</summary>
    public MapInteractionEnvelope Envelope { get; }

    /// <summary>The lowercase hex SHA-256 over the placement and asset identities, both resource digests, the float bits
    /// of position, yaw, combined scale and raise, and <see cref="MapInteractionPolicy.Hash"/>.</summary>
    public string Digest { get; }

    /// <summary>The mathematical pose retained for frame-local backend registration.</summary>
    public MapEffectiveTransform EffectiveTransform { get; internal init; }

    // Ownership validation uses authored geometry under the precise transform, before backend approximation.
    internal PhysicsShape? SourceCollider { get; init; }
    internal double SourceUnitsToMetres { get; init; }

    internal MapPlacementGeometry(string placementId, long? numericId, string assetId, PhysicsShape? collider,
        Pose worldPose, MapBox3? colliderBounds, MapInteractionEnvelope envelope, string digest)
    {
        PlacementId = placementId;
        NumericId = numericId;
        AssetId = assetId;
        Collider = collider;
        WorldPose = worldPose;
        ColliderBounds = colliderBounds;
        Envelope = envelope;
        Digest = digest;
    }
}

/// <summary>Resolves every placement of a native document to its <see cref="MapPlacementGeometry"/>.</summary>
public static class MapPlacementShapes
{
    /// <summary>The geometry of every placement in <paramref name="document"/>, ordered by ordinal placement ID. Throws
    /// <see cref="MapDocumentException"/> for unreadable or uninstallable asset shapes, a combined scale that is not
    /// finite and positive, an asset with no interaction source, and an envelope that cannot be swept.</summary>
    public static IReadOnlyList<MapPlacementGeometry> Resolve(MapResolvedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Prepare(document.Placements, document.AssetClosure, document.EffectiveResources?.Transforms);
    }

    internal static IReadOnlyList<MapPlacementGeometry> Prepare(IReadOnlyList<MapResolvedPlacement> placements,
        MapAssetClosure closure, IReadOnlyDictionary<string, MapEffectiveTransform>? transforms)
    {
        using var preparation = MapSceneAdmission.Current?.BeginPreparation();
        MapSceneAdmission.Current?.ReserveBytes(checked(256L + placements.Count * 32L));
        var assets = new Dictionary<string, MapAssetShapes>(StringComparer.Ordinal);
        var result = new List<MapPlacementGeometry>(placements.Count);
        foreach (MapResolvedPlacement placement in placements.OrderBy(p => p.PlacementId, StringComparer.Ordinal))
        {
            if (!assets.TryGetValue(placement.AssetId, out MapAssetShapes? shapes))
                assets.Add(placement.AssetId, shapes = MapAssetShapes.Read(closure, placement.AssetId));
            MapEffectiveTransform? precise = transforms?.GetValueOrDefault("Placement:" + placement.PlacementId);
            result.Add(Build(placement, shapes, precise));
        }
        if (preparation is not null) preparation.Complete(new MapPhysicsInventory().Placements(result));
        return result.AsReadOnly();
    }

    /// <summary>The tight world bounds of one resolved placement: the union of its collider at the resolved pose and
    /// its interaction envelope, excluding the registration-origin allowances in <see cref="MapPlacementGeometry.ColliderBounds"/>.
    /// Returns null when <paramref name="shapes"/> declares neither
    /// a collider nor a selection volume. Unlike <see cref="Resolve"/> it never refuses a missing interaction source,
    /// so an editor can report an edit of such a placement. Throws <see cref="ArgumentException"/> when
    /// <paramref name="shapes"/> belongs to another asset, and <see cref="MapDocumentException"/> as
    /// <see cref="Resolve"/> does for the scale, the transform and the envelope.</summary>
    public static MapBox3? Bounds(MapResolvedPlacement placement, MapAssetShapes shapes) => Bounds(placement, shapes, out _);

    /// <summary>Reports the same tight bounds as <see cref="Bounds(MapResolvedPlacement, MapAssetShapes)"/> and returns
    /// conservative installed collider and envelope bounds in <paramref name="spatialBounds"/> for edit invalidation.
    /// Both values are null when the asset declares neither a collider nor a selection volume.</summary>
    public static MapBox3? Bounds(MapResolvedPlacement placement, MapAssetShapes shapes, out MapBox3? spatialBounds)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(shapes);
        if (!string.Equals(placement.AssetId, shapes.AssetId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Shapes of asset '{shapes.AssetId}' cannot bound placement '{placement.PlacementId}' of asset '{placement.AssetId}'.",
                nameof(shapes));
        if (shapes.Collider is null && shapes.Selection is null) { spatialBounds = null; return null; }
        using var preparation = MapSceneAdmission.Current?.BeginPreparation();
        MapPlacementGeometry geometry = Build(placement, shapes);
        MapBox3 envelope = geometry.Envelope.Bounds;
        spatialBounds = geometry.ColliderBounds is { } conservative ? conservative.Union(envelope) : envelope;
        return geometry.Collider is { } collider ? MapShapeBounds.Of(collider, geometry.WorldPose).Union(envelope) : envelope;
    }

    static MapPlacementGeometry Build(MapResolvedPlacement placement, MapAssetShapes shapes, MapEffectiveTransform? precise = null)
    {
        using var preparation = MapSceneAdmission.Current?.BeginPreparation();
        MapTransform transform = placement.Transform;
        MapEffectiveTransform effective = precise ?? MapEffectiveTransform.From(transform);
        float scale = (float)(shapes.SourceUnitsToMetres * effective.Scale);
        if (!float.IsFinite(scale) || scale <= 0f)
            throw new MapDocumentException(
                $"Placement '{placement.PlacementId}' combined scale {scale.ToString(CultureInfo.InvariantCulture)} is not finite and positive.");
        if (!float.IsFinite(transform.Position.X) || !float.IsFinite(transform.Position.Y) ||
            !float.IsFinite(transform.Position.Z) || !float.IsFinite(transform.YawRadians))
            throw new MapDocumentException($"Placement '{placement.PlacementId}' has a nonfinite position or yaw.");

        MapSceneAdmission.Current?.BeforeGeometry(MapPlacementPreparation.InstanceBytes(placement, shapes, scale));
        var worldPose = MapBackendPoseAdapter.Pose(effective, WorldFrame.Origin);
        PhysicsShape? collider = shapes.Collider is null ? null : PhysicsShapeScale.Uniform(shapes.Collider, scale);
        PhysicsShape? selection = shapes.Selection is null ? null : PhysicsShapeScale.Uniform(shapes.Selection, scale);
        PhysicsShape source = selection ?? collider ?? throw new MapDocumentException(
            $"Placement '{placement.PlacementId}' asset '{shapes.AssetId}' has no interaction source: it declares neither a selection volume nor a collider.");

        MapBox3? colliderBounds = null;
        if (collider is not null) colliderBounds = MapRegistrationBounds.Of(collider, effective);
        MapInteractionEnvelope envelope =
            MapInteractionEnvelope.Build(placement.PlacementId, source, selection is null, worldPose);
        string digest = Digest(placement, shapes, scale, envelope.RaiseMetres, precise);
        var result = new MapPlacementGeometry(placement.PlacementId, placement.NumericId, shapes.AssetId, collider, worldPose,
            colliderBounds, envelope, digest)
        { EffectiveTransform = effective, SourceCollider = shapes.Collider, SourceUnitsToMetres = shapes.SourceUnitsToMetres };
        if (preparation is not null)
        {
            var inventory = new MapPhysicsInventory();
            // The decoded asset owns these shared inputs until the placement collection takes its output.
            _ = inventory.Shape(shapes.Collider); _ = inventory.Shape(shapes.Selection);
            preparation.Complete(inventory.Placement(result));
        }
        return result;
    }

    /// <summary>The rotation by <paramref name="yaw"/> radians about world Y, (0, sin, 0, cos) of half the yaw. The
    /// half-angle sine and cosine are taken in double and rounded to float once, so the pose does not depend on the
    /// platform's float sine and cosine.</summary>
    internal static Quaternion YawOrientation(float yaw)
    {
        (double sin, double cos) = Math.SinCos(yaw * 0.5d);
        return new Quaternion(0f, (float)sin, 0f, (float)cos);
    }

    static string Digest(MapResolvedPlacement placement, MapAssetShapes shapes, float scale, float raise, MapEffectiveTransform? precise)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[8];
        WriteString(hash, placement.PlacementId);
        buffer[0] = placement.NumericId is null ? (byte)0 : (byte)1;
        hash.AppendData(buffer[..1]);
        BinaryPrimitives.WriteInt64LittleEndian(buffer, placement.NumericId ?? 0);
        hash.AppendData(buffer);
        WriteString(hash, shapes.AssetId);
        WriteString(hash, shapes.ColliderSha256);
        WriteString(hash, shapes.SelectionSha256);
        Vector3 position = placement.Transform.Position;
        foreach (float value in new[] { position.X, position.Y, position.Z, placement.Transform.YawRadians, scale, raise })
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, BitConverter.SingleToInt32Bits(value));
            hash.AppendData(buffer[..4]);
        }
        WriteString(hash, MapInteractionPolicy.Hash);
        if (precise is { } pose && pose != MapEffectiveTransform.From(placement.Transform))
            foreach (double value in new[] { pose.X, pose.Y, pose.Z, pose.YawRadians, pose.Scale })
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer, BitConverter.DoubleToInt64Bits(value));
                hash.AppendData(buffer);
            }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // A length prefix keeps adjacent fields unambiguous. A null string writes length -1.
    static void WriteString(IncrementalHash hash, string? value)
    {
        Span<byte> length = stackalloc byte[4];
        if (value is null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(length, -1);
            hash.AppendData(length);
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

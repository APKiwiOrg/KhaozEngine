using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.Physics;

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

    /// <summary>The placement position with its yaw about world Y.</summary>
    public Pose WorldPose { get; }

    /// <summary>The world bounds of <see cref="Collider"/> at <see cref="WorldPose"/>, or null without a collider.</summary>
    public MapBox3? ColliderBounds { get; }

    /// <summary>The interaction envelope under <see cref="MapInteractionPolicy"/>.</summary>
    public MapInteractionEnvelope Envelope { get; }

    /// <summary>The lowercase hex SHA-256 over the placement and asset identities, both resource digests, the float bits
    /// of position, yaw, combined scale and raise, and <see cref="MapInteractionPolicy.Hash"/>.</summary>
    public string Digest { get; }

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
        var assets = new Dictionary<string, MapAssetShapes>(StringComparer.Ordinal);
        var result = new List<MapPlacementGeometry>(document.Placements.Count);
        foreach (MapResolvedPlacement placement in document.Placements.OrderBy(p => p.PlacementId, StringComparer.Ordinal))
        {
            if (!assets.TryGetValue(placement.AssetId, out MapAssetShapes? shapes))
                assets.Add(placement.AssetId, shapes = MapAssetShapes.Read(document.AssetClosure, placement.AssetId));
            result.Add(Build(placement, shapes));
        }
        return result.AsReadOnly();
    }

    static MapPlacementGeometry Build(MapResolvedPlacement placement, MapAssetShapes shapes)
    {
        MapTransform transform = placement.Transform;
        float scale = shapes.SourceUnitsToMetres * transform.Scale;
        if (!float.IsFinite(scale) || scale <= 0f)
            throw new MapDocumentException(
                $"Placement '{placement.PlacementId}' combined scale {scale.ToString(CultureInfo.InvariantCulture)} is not finite and positive.");
        if (!float.IsFinite(transform.Position.X) || !float.IsFinite(transform.Position.Y) ||
            !float.IsFinite(transform.Position.Z) || !float.IsFinite(transform.YawRadians))
            throw new MapDocumentException($"Placement '{placement.PlacementId}' has a nonfinite position or yaw.");

        var worldPose = new Pose(transform.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, transform.YawRadians));
        PhysicsShape? collider = shapes.Collider is null ? null : PhysicsShapeScale.Uniform(shapes.Collider, scale);
        PhysicsShape? selection = shapes.Selection is null ? null : PhysicsShapeScale.Uniform(shapes.Selection, scale);
        PhysicsShape source = selection ?? collider ?? throw new MapDocumentException(
            $"Placement '{placement.PlacementId}' asset '{shapes.AssetId}' has no interaction source: it declares neither a selection volume nor a collider.");

        MapBox3? colliderBounds = collider is null ? null : MapShapeBounds.Of(collider, worldPose);
        MapInteractionEnvelope envelope =
            MapInteractionEnvelope.Build(placement.PlacementId, source, selection is null, worldPose);
        string digest = Digest(placement, shapes, scale, envelope.RaiseMetres);
        return new MapPlacementGeometry(placement.PlacementId, placement.NumericId, shapes.AssetId, collider, worldPose,
            colliderBounds, envelope, digest);
    }

    static string Digest(MapResolvedPlacement placement, MapAssetShapes shapes, float scale, float raise)
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

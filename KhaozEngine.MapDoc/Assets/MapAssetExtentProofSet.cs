using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace KhaozEngine.MapDoc.Assets;

public sealed record MapAssetResourceExtentProof(string ResourceId, string Sha256, MapLocalBounds DeclaredBounds, MapLocalBounds VerifiedBounds);

/// <summary>Only conservative bounds and verified provenance. No decoded geometry or images are retained.</summary>
public sealed class MapAssetExtentProof
{
    public string AssetId { get; }
    public float SourceUnitsToMetres { get; }
    public IReadOnlyList<MapAssetResourceExtentProof> Resources { get; }
    public MapLocalBounds? DeclaredLightBounds { get; }
    public MapLocalBounds? VerifiedLightBounds { get; }
    public int ValidationVersion { get; }
    internal MapAssetExtentProof(string assetId, float sourceUnitsToMetres, IReadOnlyList<MapAssetResourceExtentProof> resources,
        MapLocalBounds? declaredLightBounds, MapLocalBounds? verifiedLightBounds, int validationVersion)
    {
        AssetId = assetId; SourceUnitsToMetres = sourceUnitsToMetres;
        Resources = Array.AsReadOnly(resources.ToArray()); DeclaredLightBounds = declaredLightBounds;
        VerifiedLightBounds = verifiedLightBounds; ValidationVersion = validationVersion;
    }
}

public sealed class MapAssetExtentProofSet
{
    public string ClosureHash { get; }
    public string Digest { get; }
    public IReadOnlyDictionary<string, MapAssetExtentProof> ByAsset { get; }
    public long MetadataBytes { get; }
    internal MapAssetExtentProofSet(string closureHash, string digest, IReadOnlyList<MapAssetExtentProof> rows, long metadataBytes)
    {
        ClosureHash = closureHash; Digest = digest; MetadataBytes = metadataBytes;
        ByAsset = new ReadOnlyDictionary<string, MapAssetExtentProof>(rows.ToDictionary(r => r.AssetId, StringComparer.Ordinal));
    }
}

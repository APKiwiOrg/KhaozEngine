using System;

namespace KhaozEngine.Attachments;

public enum ClothSurfaceRefusal
{
    Json,
    Version,
    Count,
    Topology,
    Material,
    MeshDigest,
    AssetRead,
    AssetDigest,
    SourcePoint,
    SourceInfluence,
    PinMovesWithCloth,
    RenderEmbedding,
    RenderOrder,
    RoundTrip,
    FittedTriangle,
    ModelPose,
    HostPose,
    GarmentPose,
    UnembeddedFallbackVertex,
    FallbackHierarchy,
    SourceGeometry,
    MaterialEmbedding,
    MaterialRenderOrder,
}

/// <summary>A cloth sidecar or fitted binding refusal with its source and offending authored element.</summary>
public sealed class ClothSurfaceDataException : MalformedDataException
{
    public ClothSurfaceDataException(ClothSurfaceRefusal reason, string sourceName, string message, Exception? inner = null)
        : base($"{sourceName}: {message}", inner)
    {
        Reason = reason;
        SourceName = sourceName;
    }

    public ClothSurfaceRefusal Reason { get; }
    public string SourceName { get; }
    public uint? PartId { get; init; }
    public int? ElementIndex { get; init; }
    public string? JsonPath { get; init; }
    public float? ErrorMetres { get; init; }
}

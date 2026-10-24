using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>Detached CPU input for one profile ground or cover build. Workers read only the input.</summary>
internal abstract record TileProfileBuildInput;

/// <summary>The CPU result of one profile build, applied on the scene thread.</summary>
internal abstract record TileProfileBuildOutput;

/// <summary>One region-plane's ground snapshot, materials and mesher settings.</summary>
internal sealed record TileGroundBuildInput(
    TileWorldDocument Document,
    TileWorldCatalogs Catalogs,
    RegionCoord Region,
    int Plane,
    TileGroundLod Lod,
    TileGroundMesherOptions Options, TileWaterPreparation Water, bool CaptureOpticalSurface) : TileProfileBuildInput;

/// <summary>A built ground mesh, null when the region-plane has no drawable tile.</summary>
internal sealed record TileGroundBuildOutput(TileGroundCpuBuild Ground, TileWaterPreparation Water) : TileProfileBuildOutput;

/// <summary>A built region cover batch.</summary>
internal sealed record TileCoverBuildOutput(IReadOnlyList<GroundCoverInstance> Cover) : TileProfileBuildOutput;

// The one background owner of profile ground and cover. Every request carries a view-wide generation, so a
// result can only land on the residency, plane and kind that asked for it, even across demotion and reload.
public sealed partial class TileWorldView
{
    readonly TileWorldBuildQueue<TileProfileBuildInput, TileProfileBuildOutput> _profileBuilds;
    // The headless witness of every ground and cover CPU build, synchronous or worker. Null outside tests.
    readonly Action<TileWorldBuildKey>? _onCpuBuild;
    long _profileGeneration;

    /// <summary>Profile ground and cover builds still queued, running or awaiting their frame apply.</summary>
    internal int TrackedProfileBuilds => _profileBuilds.TrackedCount;

    /// <summary>Ground ring and material captures taken on the frame thread for profile builds.</summary>
    internal int GroundCaptures { get; private set; }

    internal int MaterialCaptures { get; private set; }

    long NextProfileGeneration() => ++_profileGeneration;

    TileWorldDocument CaptureGround(RegionCoord region)
    {
        EnsureWaterSources(region);
        GroundCaptures++;
        return TileWorldBuildSnapshot.CaptureGround(_doc, region, GroundSurfaceRequested);
    }

    TileWorldCatalogs CaptureMaterials()
    {
        MaterialCaptures++;
        return TileWorldBuildSnapshot.CaptureMaterials(_catalogs);
    }

    // Runs on a worker, or inline only for an explicit prime. Reads nothing but the detached input.
    TileProfileBuildOutput BuildProfileCpu(TileWorldBuildRequest<TileProfileBuildInput> request)
    {
        _onCpuBuild?.Invoke(request.Key);
        return request.Input switch
        {
            TileGroundBuildInput ground => new TileGroundBuildOutput(TileGroundCpuBuild.Build(
                ground.Document, ground.Catalogs, ground.Region, ground.Plane, ground.Lod, ground.Options,
                ground.CaptureOpticalSurface), ground.Water),
            TileCoverBuildInput cover => new TileCoverBuildOutput(cover.Build()),
            _ => throw new InvalidOperationException($"Unknown profile build input {request.Input.GetType().Name}."),
        };
    }

    void ApplyProfileBuild(TileWorldBuildResult<TileProfileBuildOutput> result)
    {
        switch (result.Payload)
        {
            case TileGroundBuildOutput ground:
                ApplyGround(result.Key, result.Generation, ground.Ground, ground.Water);
                break;
            case TileCoverBuildOutput cover:
                ApplyCover(result.Key.Region, result.Generation, cover.Cover);
                break;
        }
    }

    void PumpProfileBuilds(Vector3 focus)
    {
        int x = (int)MathF.Floor(TileWorldSpace.TileX(focus.X, _doc.TileSize));
        int z = (int)MathF.Floor(TileWorldSpace.TileZ(focus.Z, _doc.TileSize));
        _profileBuilds.Pump(RegionCoord.Of(x, z));
    }
}

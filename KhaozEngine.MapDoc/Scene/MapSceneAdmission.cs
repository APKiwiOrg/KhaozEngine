using System;
using KhaozEngine.MapDoc.Foliage;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>One synchronous acquisition allowance, shared by every nested session, copy and compilation.</summary>
internal sealed class MapSceneAdmission(MapSceneLimits limits, Action<string>? entering = null, MapFoliageContextLimits? foliageLimits = null,
    string preparationLimitName = nameof(MapSceneLimits.MaxPreparedBytes))
{
    readonly MapFoliageContextLimits? _foliageLimits = foliageLimits ?? limits.Preparation?.FoliageContext;
    [ThreadStatic] static MapSceneAdmission? t_current;
    internal static MapSceneAdmission? Current => t_current;
    readonly MapPreparationLedger _bytes = new(limits.MaxPreparedBytes, preparationLimitName);
    internal long RemainingPreparedBytes => _bytes.Remaining;
    internal long WorkspaceBytes => _bytes.WorkspaceBytes;
    internal long RetainedBytes => _bytes.RetainedBytes;
    internal long PeakBytes => _bytes.PeakBytes;
    internal long RemainingPreparedFaces => limits.MaxPreparedFaces - PreparedFaces;
    internal int RemainingPatchScopes => limits.MaxPatchScopes - PatchScopes;
    internal MapPreparationReservation Workspace() => _bytes.Workspace();
    internal MapPreparationScope? PreparationScope { get; set; }
    internal MapPreparationScope BeginPreparation() => new(this);
    internal PreparationWindow LimitPreparation(long bytes, string limitName = nameof(MapSceneLimits.MaxPreparedBytes), long reused = 0)
        => new(_bytes.OpenWindow(bytes, limitName, reused));
    internal MapSceneCapacity PreparedByteCapacity(decimal bytes) => _bytes.Capacity(bytes);
    internal sealed class PreparationWindow(MapPreparationLedger.Window window) : IDisposable
    {
        internal void ChargeExternal(long bytes) => window.ChargeExternal(bytes);
        public void Dispose() => window.Dispose();
    }
    internal void ReleaseUnusedBytes(long bytes)
    { if (PreparationScope is { } scope) scope.Release(bytes); else _bytes.ReleaseLegacy(bytes); }
    internal long PreparedFaces { get; private set; }
    internal long PayloadReads { get; private set; }
    internal long FoliageSourceUtf8Bytes { get; private set; }
    internal long FoliageChunks { get; private set; }
    internal long FoliagePlacements { get; private set; }
    internal long FoliagePatches { get; private set; }
    internal long FoliagePaintTargets { get; private set; }
    readonly HashSet<string> _paintTargets = new(StringComparer.Ordinal);
    internal void BeforePaintTarget(string id)
    {
        if (!_paintTargets.Add(id)) return;
        if (_foliageLimits is not null && FoliagePaintTargets >= _foliageLimits.MaxPaintTargets) throw new MapSceneCapacityException(nameof(MapFoliageContextLimits.MaxPaintTargets), (long)FoliagePaintTargets + 1, _foliageLimits.MaxPaintTargets);
        FoliagePaintTargets++;
    }
    readonly HashSet<MapSceneResourceKey> _resources = new();
    internal void BeforeResource(MapSceneResourceKey key)
    {
        if (_resources.Contains(key)) return;
        if (_resources.Count >= limits.MaxSelectedResources) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxSelectedResources), (long)_resources.Count + 1, limits.MaxSelectedResources);
        if (key.Kind == "Foliage")
        {
            if (_foliageLimits is not null && FoliageChunks >= _foliageLimits.MaxChunkDescriptors) throw new MapSceneCapacityException(nameof(MapFoliageContextLimits.MaxChunkDescriptors), (long)FoliageChunks + 1, _foliageLimits.MaxChunkDescriptors);
            FoliageChunks++;
        }
        if (key.Kind == "Placement")
        {
            if (_foliageLimits is not null && FoliagePlacements >= _foliageLimits.MaxPlacements) throw new MapSceneCapacityException(nameof(MapFoliageContextLimits.MaxPlacements), (long)FoliagePlacements + 1, _foliageLimits.MaxPlacements);
            FoliagePlacements++;
        }
        if (key.Kind == "Patch")
        {
            if (_foliageLimits is not null && FoliagePatches >= _foliageLimits.MaxSurfacePatches) throw new MapSceneCapacityException(nameof(MapFoliageContextLimits.MaxSurfacePatches), (long)FoliagePatches + 1, _foliageLimits.MaxSurfacePatches);
            FoliagePatches++;
        }
        _resources.Add(key);
    }
    internal int PatchScopes { get; private set; }

    // Lower-level released clone and compile entry points have no scope argument. The synchronous extent of
    // an acquisition supplies their same allowance, then restores its predecessor even after a refusal.
    internal IDisposable Enter() => new Activation(this);
    sealed class Activation : IDisposable
    {
        readonly MapSceneAdmission? _previous = t_current;
        internal Activation(MapSceneAdmission value) => t_current = value;
        public void Dispose() => t_current = _previous;
    }

    internal static bool Supports(IMapSurfaceSource source) => source is MapDocumentSurfaceSource or MapStoredSurfaceSource or MapEffectiveSurfaceSource ||
        source is MapComposedSurfaceSource composed && composed.SupportsSceneAdmission || source is MapSceneSurfaceSource scoped && Supports(scoped.Inner);

    internal void BeforeScope(IMapSurfaceSource source)
    {
        if (!Supports(source)) throw new MapSceneSourceException(MapSceneStatus.Invalid, "Surface source cannot participate in scene admission.");
        if (PatchScopes >= limits.MaxPatchScopes) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxPatchScopes), (long)PatchScopes + 1, limits.MaxPatchScopes);
        PatchScopes++;
    }

    internal bool CanReservePreparation(long bytes, long faces) => bytes >= 0 && faces >= 0 &&
        bytes <= RemainingPreparedBytes && faces <= limits.MaxPreparedFaces - PreparedFaces;
    internal MapSceneCapacity? PreparationRefusal(long bytes, long faces)
    {
        if (bytes < 0 || bytes > RemainingPreparedBytes) return PreparedByteCapacity(bytes);
        return faces < 0 || faces > RemainingPreparedFaces
            ? new(nameof(MapSceneLimits.MaxPreparedFaces), (decimal)PreparedFaces + faces, limits.MaxPreparedFaces) : null;
    }
    internal bool TryReservePreparation(long bytes, long faces) => TryReservePreparation(bytes, faces, out _);
    internal bool TryReservePreparation(long bytes, long faces, out MapSceneCapacity? capacity)
    {
        capacity = PreparationRefusal(bytes, faces);
        if (capacity is not null) return false;
        ReserveBytes(bytes);
        PreparedFaces += faces;
        return true;
    }

    // A prepared product already charged its complete demand before creating any payload.
    internal static Suspension Suspend() => new(t_current);
    internal readonly struct Suspension : IDisposable
    {
        readonly MapSceneAdmission? _previous;
        internal Suspension(MapSceneAdmission? previous) { _previous = previous; t_current = null; }
        public void Dispose() => t_current = _previous;
    }

    internal void ReserveBytes(long bytes)
    { if (PreparationScope is { } scope) scope.Reserve(bytes); else _bytes.ReserveLegacy(bytes); }

    internal void BeforeInputBytes(long bytes)
    {
        if (_foliageLimits is not null && bytes > _foliageLimits.MaxSourceUtf8Bytes - FoliageSourceUtf8Bytes) throw new MapSceneCapacityException(nameof(MapFoliageContextLimits.MaxSourceUtf8Bytes), (decimal)FoliageSourceUtf8Bytes + bytes, _foliageLimits.MaxSourceUtf8Bytes);
        ReserveBytes(bytes);
        FoliageSourceUtf8Bytes = checked(FoliageSourceUtf8Bytes + bytes);
    }

    internal void BeforePayload(long bytes)
    {
        BeforeInputBytes(bytes);
        PayloadReads = checked(PayloadReads + 1);
        entering?.Invoke("payload");
    }

    internal void BeforeGeometry(long bytes)
    {
        ReserveBytes(bytes);
        entering?.Invoke("geometry");
    }

    internal void BeforeClone(MapSurfacePatch patch)
    {
        ReserveBytes(PatchBytes(patch));
        entering?.Invoke("clone");
    }

    internal void BeforeCompile(MapSurfacePatch patch, MapSlotCellMask cells, MapSurfaceRef surface, bool logical)
    {
        long faces = 0;
        for (int z = 0; z < patch.Depth; z++)
            for (int x = 0; x < patch.Width; x++)
            {
                int slot = (z + patch.CellMinZ) * 64 + x + patch.CellMinX;
                if (!cells.Contains(slot) || !patch.IsPresent(x, z)) continue;
                // Count the actual subdivision demand without the compiler's subdivision array allocation.
                // Logical fallback cells conservatively admit every possible parent child.
                faces = checked(faces + (logical && MapSurfaceCompiler.Fallback(surface, patch.Cells[z * patch.Width + x])
                    ? 4L * MapSurfaceCompiler.MaxChildrenPerParent : MapSurfaceCompiler.CountFaces(surface, patch, slot)));
            }
        ReserveCompilation(faces);
    }

    internal void ReserveCompilation(long faces)
    {
        if (faces < 0 || faces > limits.MaxPreparedFaces - PreparedFaces) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxPreparedFaces), (decimal)PreparedFaces + faces, limits.MaxPreparedFaces);
        long bytes = checked(faces * 1280);
        ReserveBytes(bytes);
        PreparedFaces = checked(PreparedFaces + faces);
        entering?.Invoke("compile");
    }

    /// <summary>Copied payload values, excluding shared immutable strings and CLR object headers.</summary>
    internal static long PatchBytes(MapSurfacePatch patch)
    {
        long bytes = checked(4L * patch.Heights.Length + 8L * patch.Cells.Length + 8L * patch.Presence.Length +
            (long)Unsafe.SizeOf<MapCornerDependency>() * patch.CornerDependencies.Count +
            (long)Unsafe.SizeOf<MapEdgeSubdivision>() * patch.EdgeSubdivisions.Count + IntPtr.Size * (long)patch.Records.Count);
        foreach (var record in patch.Records)
            bytes = checked(bytes + (record switch
            {
                MapSurfaceSeam seam => (long)Unsafe.SizeOf<(MapLatticeVertex, MapLatticeVertex)>() * seam.Pairs.Count,
                MapBoundaryChain chain => IntPtr.Size * (long)chain.Vertices.Count,
                MapCavePortal portal => (long)Unsafe.SizeOf<MapLatticeVertex>() * portal.Interval.Count,
                MapHorizontalOpening opening => 4L * opening.SlotCells.Count,
                MapSpaceFootprint footprint => 4L * footprint.SlotCells.Count,
                MapVerticalLink link => IntPtr.Size * (long)(link.Openings.Count + link.Portals.Count + link.GeometryOwners.Count),
                MapSpaceDoc space => IntPtr.Size * (long)(space.DomainTags.Count + space.Walls.Count + space.Portals.Count + space.Links.Count),
                MapPrismDomain prism => 32L * prism.Footprint.Rects.Count + IntPtr.Size * (long)prism.Footprint.Rings.Count +
                    RingBytes(prism.Footprint),
                _ => 0,
            }));
        return bytes;
    }

    static long RingBytes(MapWaterDomainDoc domain)
    {
        long bytes = 0;
        foreach (var ring in domain.Rings) bytes = checked(bytes + (long)Unsafe.SizeOf<MapLatticeAddress>() * ring.Vertices.Count);
        return bytes;
    }
}

internal sealed class MapSceneSourceException(MapSceneStatus status, string message, MapSceneCapacity? capacity = null) : Exception(message)
{
    internal MapSceneStatus Status { get; } = status;
    internal MapSceneCapacity? Capacity { get; } = capacity;
}

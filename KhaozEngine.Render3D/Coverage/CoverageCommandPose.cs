using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Immutable current command frames and authored parameters shared by carry preparation and offline validation.
/// Matrices use row vectors. CoverageWorld is the canonical float composition Support.CoverageLocal * EquipmentWorld.
/// TransitionWeight scales the selected full recession. Geometry and recession budgets are verified offline.</summary>
public readonly record struct CoverageCommandPose
{
    public CoverageCommandPose(uint id, ulong equipmentInstanceId, CoverageRefinement topology, int regionIndex,
        CoverageSupportSurface support, Matrix4x4 equipmentWorld, Matrix4x4 coverageWorld, float minZ, float maxZ,
        float collarWidth, float clearance, float maxWorldRecession, float transitionWeight = 1)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(support);
        Id = id; EquipmentInstanceId = equipmentInstanceId; Topology = topology; RegionIndex = regionIndex; Support = support;
        EquipmentWorld = equipmentWorld; CoverageWorld = coverageWorld; MinZ = minZ; MaxZ = maxZ; CollarWidth = collarWidth;
        Clearance = clearance; MaxWorldRecession = maxWorldRecession; TransitionWeight = transitionWeight;
    }

    public uint Id { get; init; }
    public ulong EquipmentInstanceId { get; init; }
    /// <summary>The exact compiled host topology instance this command was bound to. A label match is not identity.</summary>
    public CoverageRefinement Topology { get; init; }
    public int RegionIndex { get; init; }
    public CoverageSupportSurface Support { get; init; }
    public Matrix4x4 EquipmentWorld { get; init; }
    public Matrix4x4 CoverageWorld { get; init; }
    public float MinZ { get; init; }
    public float MaxZ { get; init; }
    public float CollarWidth { get; init; }
    public float Clearance { get; init; }
    public float MaxWorldRecession { get; init; }
    public float TransitionWeight { get; init; }
}

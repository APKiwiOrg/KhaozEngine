using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

internal sealed class CoverageFrameArena
{
    internal readonly List<CoveragePreparation> Preparations = new();
    internal readonly List<CoverageEquipmentStage> Equipment = new();
    internal int EquipmentCount;
    internal ulong Generation;
    internal ulong NextPreparation;
    internal bool Closed;
    internal void Begin()
    {
        Generation++;
        Preparations.Clear();
        EquipmentCount = 0;
        Closed = false;
    }
}

internal sealed class CoverageEquipmentStage
{
    internal Scene3D Scene = null!;
    internal ulong Generation;
    internal MotionKey Actor;
    internal ulong Instance;
    internal readonly CoverageEquipmentCommand[] Commands = new CoverageEquipmentCommand[CoverageScratch.MaxCommands];
    internal int CommandCount;
    internal RigidInstanceDraw[] Draws = [];
    internal bool[] Submitted = [], Outlined = [];
    internal int Count;

    internal bool TryCommand(uint id, out CoverageSupportSurface support)
    {
        for (int i = 0; i < CommandCount; i++)
            if (Commands[i].CommandId == id) { support = Commands[i].Support; return true; }
        support = null!;
        return false;
    }
}

/// <summary>The render role of one captured coverage certification context.</summary>
internal enum CoveragePassRole { Main, Motion, Outline, Cascade, PointFace }

internal sealed class CoveragePreparation
{
    internal readonly SkinnedHostConformer Owner;
    internal readonly ModelVertex[] Posed, Before, Candidate;
    internal readonly Matrix4x4[] Palette;
    internal readonly CoverageCommandPose[] Commands = new CoverageCommandPose[CoverageScratch.MaxCommands];
    internal readonly CoverageEquipmentStage[] Equipment = new CoverageEquipmentStage[CoverageScratch.MaxCommands];
    internal readonly List<CoverageRenderPass> Passes = new();
    /// <summary>Captured certification contexts by role, host roles first, then equipment roles at
    /// <see cref="EquipmentRoleOffset"/>. Tagged at capture, so aliased matrices never merge two roles.</summary>
    internal readonly int[] PassRoles = new int[2 * EquipmentRoleOffset];
    internal const int EquipmentRoleOffset = 5;
    internal readonly SkinnedInstanceDraw?[] Draws, Outlines;
    internal readonly int[] BaseVertices;
    internal ulong Generation, Id, View;
    internal MotionKey Actor;
    internal Matrix4x4 Model;
    internal int CommandCount;
    internal MeshBounds UnionBounds, CandidateBounds, WorldBounds;
    internal CoverageGpuReservation GpuReservation;
    internal readonly uint[] ResultIdentity = new uint[8];
    internal double CandidateAttributes, CandidateOutlineAttributes;
    internal PointShadowCasterSphere Sphere;
    internal ulong SphereView;
    internal CoverageFrameOutcome Outcome;
    internal CoverageCertificate Certificate;
    internal ulong MotionRevision;
    internal bool MotionReusable, MotionPresented;
    internal CoveragePreparation(SkinnedHostConformer owner, int bones, int materials)
    {
        Owner = owner;
        Posed = new ModelVertex[owner.Topology.SourceVertexCount];
        Before = new ModelVertex[owner.Topology.VertexCount];
        Candidate = new ModelVertex[owner.Topology.VertexCount];
        Palette = new Matrix4x4[bones];
        Draws = new SkinnedInstanceDraw?[materials]; Outlines = new SkinnedInstanceDraw?[materials];
        BaseVertices = new int[materials];
    }
    internal ReadOnlySpan<ModelVertex> Final => Outcome.Presentation == CoveragePresentation.Covered ? Candidate : Posed;
}

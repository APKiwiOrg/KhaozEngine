using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Interiors;

/// <summary>Hand-enumerated simultaneous owners for the one-cell, untransformed prism fixture.</summary>
internal static class MapMembershipFullPolicyInventory
{
    internal const long BorrowedScene = 1000000, External = 29, Reused = 73, Historical = 50000000;
    internal static long Prior => 4096 + 12640 + 256 + 2 * Unsafe.SizeOf<MapMembershipPart>() + 5 - 2816;
    internal static long Header => 4096 + 12640 + 2 * 512 + 4 * 4096 + 16 * (5 + 5 + 4 + 5) +
        16 * 4 * 4 + 256 + 2 * (2 * Unsafe.SizeOf<MapMembershipPart>() + 5) + 1;

    internal readonly record struct Boundary(string Stage, string Owners, long Bytes);

    // No production capacity calculator, geometry compiler, receipt or diagnostic supplies an expectation.
    // The fixture has two unit-square cells, four records, one four-vertex prism and no transforms.
    internal static IReadOnlyList<Boundary> Boundaries()
    {
        long h = Header;
        const long controls = 32768 + 256 * 4, domain = 4096 + 256 * 4;
        const long cellKernel = 4096 + 4096 * 8 + 2 * 256;
        const long refinementKernel = 4096 + 4096 * (4 + 3 * (2 + 2)) + 256 * (2 * 2);
        const long rawTriangle = 1024 + 256 * 3, rootRaw = 2 * rawTriangle, allRaw = 4 * rawTriangle;
        const long compilerEnvelope = 2 * 1280, subdivision = 16;
        // These short synthetic identities fit the canonical writer's initial buffer without growth.
        const long canonicalHash = 8192;
        long compiled = 4L * (Unsafe.SizeOf<MapVertexId>() + Unsafe.SizeOf<MapExactPoint>() + Unsafe.SizeOf<Vector3>()) +
            2L * (Unsafe.SizeOf<MapCompiledFace>() + Unsafe.SizeOf<MapPaintCoverage>());
        // Both validated patches, pending masks, compiled patches and copied bound-face rows survive refinement.
        long context = 2 * (128 + 1024 + compiled + 384 * 2);
        const long parentIndex = 512 + 512 * 2, roomIndex = 512 + 512, parts = 128 + 512;
        const long roomRows = 4 * 4, bounds = 512 + 256 * 4, peerIndex = 512 + 512 * 4;
        long validation = h + controls + domain;
        const long plannerControls = 1024 + 128 * 4, patchIndex = 256 * 2, pending = 2304, bound = 128 + 32;
        const long planning = plannerControls + 2 * bound + 2 * 640, publication = 4096 + 64 * 4;
        const long arithmetic = 32768;
        // Every normalized coefficient, including zero, occupies one magnitude byte on these unit squares.
        const long affine = 64 + 4 * (48 + 1), polygon = 128 + 45 * 4 + 4 * affine;
        const long triangle = 219 + 4 * affine, cellBase = 256 + 256 * 2;
        const long cell = cellBase + polygon + 2 * triangle, geometry = 512 + 256 * 2 + 64 * 2;
        const long collections = 1024 + 32 * 2 + 96 * 2, cellIndexes = 2 * (64 + 8 + 96);
        long groups = 1024 + 256 + 2L * Unsafe.SizeOf<MapCompiledFace>() +
            2L * (3 * Unsafe.SizeOf<MapCompiledFace>() + Unsafe.SizeOf<MapFaceKey>() + 4);
        long construction = h + planning + publication;
        long footprint = geometry + 2 * cell + cellIndexes + 128 + 8;
        long prism = footprint + polygon + 2 * (128 + 192);
        return new Boundary[]
        {
            new("Header", "header and borrowed scene", h),
            new("Validation", "controls, domain and domain compiler", validation + 4096 + 4096 * 4),
            new("Validation", "context demand masks and footprint kernel", validation + 2 * (128 + 1024) + cellKernel),
            new("Validation", "first compiled patch, second compiler and bound rows",
                validation + 2 * (128 + 1024) + compiled + 384 * 2 + compilerEnvelope + subdivision),
            new("Validation", "context, footprint/refinement kernels, both parent triangles and boundary hashing",
                validation + context + cellKernel + refinementKernel + rootRaw + canonicalHash),
            new("Validation", "parent triangles, second domain and convex descriptors",
                validation + rootRaw + domain + parentIndex + parts + 4096 + 4096 * 4 + 256 * 2),
            new("Validation", "parent/room triangles, second domain, indexes, candidate row, clipping and identity hashing",
                validation + allRaw + domain + parentIndex + parts + roomIndex + roomRows +
                4096 + 4096 * (3 + 4 + 4) + canonicalHash),
            // The square minus its first parent triangle leaves one triangle. Both replacement generations coexist.
            new("Validation", "raw pieces, bounds, parts, convex kernel, parent row and first fragment replacement",
                validation + allRaw + bounds + parts + 4096 + 4096 * 4 + parentIndex + roomRows +
                128 + (128 + 128 + 256 * 3 * (4 + 3))),
            new("Validation", "raw pieces, bounds, parts, peer index and query growth",
                validation + allRaw + bounds + parts + peerIndex + 128 + 64 * 3 + 16 * 4),
            // Discovery ends before the new frozen association row is allocated.
            new("Planner", "lower discovery, temporary association and pending mask",
                h + plannerControls + patchIndex + arithmetic + 128 + pending),
            new("Planner", "upper discovery with the surviving lower pending mask and bound row",
                h + plannerControls + patchIndex + pending + bound + arithmetic + 128 + pending),
            new("Planner", "both pending and frozen masks before builders retire",
                h + planning + patchIndex + 2 * pending),
            new("Construction", "geometry controls, collections and first compiler",
                construction + geometry + collections + compilerEnvelope + subdivision),
            new("Construction", "first cell, second compiler and grouping copies",
                construction + geometry + collections + cell + compiled + groups),
            new("Construction", "first cell, second polygon output and three nested arithmetic kernels",
                construction + geometry + collections + cell + compiled + groups + cellBase + polygon + 3 * arithmetic),
            new("Construction", "both exact cells, both indexes and last index sorting kernel",
                construction + geometry + 2 * cell + cellIndexes + collections + arithmetic),
            new("Construction", "geometry and footprint polygon output with three arithmetic kernels",
                construction + footprint + polygon + 3 * arithmetic),
            new("Construction", "geometry, footprint, bound rows, prism domain and exact prism polygon",
                construction + prism + 32768 + 1024 * 4 + polygon + 3 * arithmetic),
            new("Publication", "surviving plan, publication workspace and all exact outputs",
                construction + prism + polygon),
        };
    }

    internal static long Peak(string stage) => Boundaries().Where(b => b.Stage == stage).Max(b => b.Bytes);
    internal static long MembershipPeak => Boundaries().Max(b => b.Bytes);
}

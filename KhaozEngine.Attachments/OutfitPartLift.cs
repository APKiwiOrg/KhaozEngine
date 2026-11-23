using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Welds a lifting part across material seams and scatters the lifted positions back to upload order.</summary>
internal static class OutfitPartLift
{
    internal static (IReadOnlyList<ReadOnlyMemory<Vector3>>? Positions, float[]? TriangleLifts) Resolve(
        LiftingPartMesh mesh, float clearance, IReadOnlyList<RegionRun> runs, ReadOnlySpan<float?> belowByRegion,
        OutfitLiftCellHeights? cellHeights, LiftFootprintPart? footprint, LiftFootprint? normalFootprint,
        Span<float> largestLift, string parameterName)
    {
        IReadOnlyList<LiftMaterialMesh> materials = mesh.Materials;
        ArgumentNullException.ThrowIfNull(materials, parameterName);
        int vertexCount = 0;
        foreach (LiftMaterialMesh material in materials)
        {
            if (material.Directions.Length != material.Positions.Length || material.Bearings.Length != material.Positions.Length)
                throw new ArgumentException("Each rest position must have one lift direction and bearing.", parameterName);
            if (material.Indices.Length % 3 != 0)
                throw new ArgumentException("Material indices must contain complete triangles.", parameterName);
            vertexCount = checked(vertexCount + material.Positions.Length);
        }

        var welded = new WeldedVertex[vertexCount];
        var weldByPosition = new Dictionary<LiftVectorBits, int>();
        var sections = new OutfitLiftSections(vertexCount, normalFootprint);
        var weldIndices = new int[materials.Count][];
        Span<int> triangleSections = stackalloc int[3];
        int runIndex = 0;
        int trianglesLeft = 0;
        int partTriangle = 0;
        float? regionalBelow = null;
        UInt128 standoffBit = UInt128.Zero;
        for (int materialIndex = 0; materialIndex < materials.Count; materialIndex++)
        {
            LiftMaterialMesh material = materials[materialIndex];
            int[] materialWelds = WeldVertices(material, weldByPosition, welded, sections);
            weldIndices[materialIndex] = materialWelds;

            ReadOnlySpan<uint> indices = material.Indices.Span;
            for (int triangle = 0; triangle < indices.Length; triangle += 3)
            {
                if (trianglesLeft == 0)
                {
                    if (runIndex == runs.Count)
                        throw RunCountMismatch(parameterName);
                    RegionRun run = runs[runIndex++];
                    trianglesLeft = run.TriangleCount;
                    regionalBelow = belowByRegion[run.Region];
                    standoffBit = largestLift.IsEmpty ? UInt128.Zero : UInt128.One << run.Region;
                }

                float? heightBelow = footprint is null ? regionalBelow
                    : normalFootprint is null ? cellHeights!.HeightBelow(footprint.Triangles[partTriangle]) : null;
                for (int corner = 0; corner < 3; corner++)
                {
                    uint vertex = indices[triangle + corner];
                    if (vertex >= materialWelds.Length)
                        throw new ArgumentException("A triangle index is outside its material's positions.", parameterName);
                    ref WeldedVertex weld = ref welded[materialWelds[(int)vertex]];
                    weld.StandoffRegions |= standoffBit;
                    triangleSections[corner] = weld.Section;
                    if (heightBelow is { } height)
                        sections.Include(weld.Section, height);
                }
                if (normalFootprint is not null)
                    cellHeights!.IncludeFaceHeights(footprint!.Triangles[partTriangle], sections, triangleSections);

                trianglesLeft--;
                partTriangle++;
            }
        }
        if (trianglesLeft != 0 || runIndex != runs.Count)
            throw RunCountMismatch(parameterName);

        sections.ResolveDistances(clearance);
        bool moved = false;
        for (int index = 0; index < weldByPosition.Count; index++)
        {
            ref WeldedVertex weld = ref welded[index];
            moved |= weld.Lift(sections.Distance(weld.Section), largestLift);
        }
        if (!moved)
            return default;

        var lifted = new ReadOnlyMemory<Vector3>[materials.Count];
        for (int materialIndex = 0; materialIndex < lifted.Length; materialIndex++)
        {
            int[] materialWelds = weldIndices[materialIndex];
            var positions = new Vector3[materialWelds.Length];
            for (int vertex = 0; vertex < positions.Length; vertex++)
                positions[vertex] = welded[materialWelds[vertex]].Position;
            lifted[materialIndex] = positions;
        }
        float[]? triangleLifts = footprint is null ? null : TriangleLifts(materials, weldIndices, welded, partTriangle);
        return (Array.AsReadOnly(lifted), triangleLifts);
    }

    static int[] WeldVertices(LiftMaterialMesh material, Dictionary<LiftVectorBits, int> byPosition,
        Span<WeldedVertex> welded, OutfitLiftSections sections)
    {
        var mapping = new int[material.Positions.Length];
        ReadOnlySpan<Vector3> positions = material.Positions.Span;
        ReadOnlySpan<Vector3> directions = material.Directions.Span;
        ReadOnlySpan<float> bearings = material.Bearings.Span;
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            Vector3 position = positions[vertex];
            var key = new LiftVectorBits(position);
            if (!byPosition.TryGetValue(key, out int index))
            {
                index = byPosition.Count;
                byPosition.Add(key, index);
                // Validated bases can differ within tolerance. Use the first vertex's basis for the whole weld.
                welded[index] = new WeldedVertex(position, directions[vertex], sections.GetOrAdd(directions[vertex], bearings[vertex]));
            }
            mapping[vertex] = index;
        }
        return mapping;
    }

    static ArgumentException RunCountMismatch(string parameterName) =>
        new("The part's region runs must cover exactly the triangles in its material meshes.", parameterName);

    static float[] TriangleLifts(IReadOnlyList<LiftMaterialMesh> materials, int[][] weldIndices,
        ReadOnlySpan<WeldedVertex> welded, int triangleCount)
    {
        var lifts = new float[triangleCount];
        int partTriangle = 0;
        for (int materialIndex = 0; materialIndex < materials.Count; materialIndex++)
        {
            ReadOnlySpan<uint> indices = materials[materialIndex].Indices.Span;
            int[] materialWelds = weldIndices[materialIndex];
            for (int triangle = 0; triangle < indices.Length; triangle += 3)
            {
                float a = welded[materialWelds[(int)indices[triangle]]].Distance;
                float b = welded[materialWelds[(int)indices[triangle + 1]]].Distance;
                float c = welded[materialWelds[(int)indices[triangle + 2]]].Distance;
                lifts[partTriangle++] = MathF.Max(a, MathF.Max(b, c));
            }
        }
        return lifts;
    }

    struct WeldedVertex(Vector3 position, Vector3 direction, int section)
    {
        internal UInt128 StandoffRegions;
        internal Vector3 Position { get; private set; } = position;
        internal int Section { get; } = section;
        internal float Distance { get; private set; }

        internal bool Lift(float distance, Span<float> largestLift)
        {
            if (distance == 0)
                return false;

            // Normalize the authored direction without rounding the offset before adding it to the source.
            double x = direction.X, y = direction.Y, z = direction.Z;
            double scale = distance / Math.Sqrt(x * x + y * y + z * z);
            Vector3 liftedPosition = new((float)(Position.X + x * scale), (float)(Position.Y + y * scale),
                (float)(Position.Z + z * scale));
            // A positive lift can round away when added to the source position.
            if (new LiftVectorBits(liftedPosition) == new LiftVectorBits(Position))
                return false;
            Position = liftedPosition;
            Distance = distance;
            // Visit only the regions this weld touches, clearing the lowest bit each time.
            for (UInt128 regions = StandoffRegions; regions != UInt128.Zero; regions &= regions - UInt128.One)
            {
                int region = (int)UInt128.TrailingZeroCount(regions);
                largestLift[region] = MathF.Max(largestLift[region], distance);
            }
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Welds a lifting part across material seams and scatters the lifted positions back to upload order.</summary>
internal static class OutfitPartLift
{
    internal static IReadOnlyList<ReadOnlyMemory<Vector3>>? Resolve(LiftingPartMesh mesh, float clearance,
        IReadOnlyList<RegionRun> runs, ReadOnlySpan<float?> below, ReadOnlySpan<float?> standoff,
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
        var weldByPosition = new Dictionary<PositionBits, int>();
        var weldIndices = new int[materials.Count][];
        int runIndex = 0;
        int trianglesLeft = 0;
        float? heightBelow = null;
        UInt128 standoffBit = UInt128.Zero;
        for (int materialIndex = 0; materialIndex < materials.Count; materialIndex++)
        {
            LiftMaterialMesh material = materials[materialIndex];
            int[] materialWelds = WeldVertices(material, weldByPosition, welded);
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
                    heightBelow = below[run.Region];
                    standoffBit = standoff[run.Region].HasValue ? UInt128.One << run.Region : UInt128.Zero;
                }

                for (int corner = 0; corner < 3; corner++)
                {
                    uint vertex = indices[triangle + corner];
                    if (vertex >= materialWelds.Length)
                        throw new ArgumentException("A triangle index is outside its material's positions.", parameterName);
                    welded[materialWelds[(int)vertex]].Touch(heightBelow, standoffBit);
                }
                trianglesLeft--;
            }
        }
        if (trianglesLeft != 0 || runIndex != runs.Count)
            throw RunCountMismatch(parameterName);

        bool moved = false;
        for (int index = 0; index < weldByPosition.Count; index++)
            moved |= welded[index].Lift(clearance, largestLift);
        if (!moved)
            return null;

        var lifted = new ReadOnlyMemory<Vector3>[materials.Count];
        for (int materialIndex = 0; materialIndex < lifted.Length; materialIndex++)
        {
            int[] materialWelds = weldIndices[materialIndex];
            var positions = new Vector3[materialWelds.Length];
            for (int vertex = 0; vertex < positions.Length; vertex++)
                positions[vertex] = welded[materialWelds[vertex]].Position;
            lifted[materialIndex] = positions;
        }
        return Array.AsReadOnly(lifted);
    }

    static int[] WeldVertices(LiftMaterialMesh material, Dictionary<PositionBits, int> byPosition, Span<WeldedVertex> welded)
    {
        var mapping = new int[material.Positions.Length];
        ReadOnlySpan<Vector3> positions = material.Positions.Span;
        ReadOnlySpan<Vector3> directions = material.Directions.Span;
        ReadOnlySpan<float> bearings = material.Bearings.Span;
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            Vector3 position = positions[vertex];
            var key = new PositionBits(position);
            if (!byPosition.TryGetValue(key, out int index))
            {
                index = byPosition.Count;
                byPosition.Add(key, index);
                // Validated bases can differ within tolerance. Use the first vertex's basis for the whole weld.
                welded[index] = new WeldedVertex(position, directions[vertex], bearings[vertex]);
            }
            mapping[vertex] = index;
        }
        return mapping;
    }

    static ArgumentException RunCountMismatch(string parameterName) =>
        new("The part's region runs must cover exactly the triangles in its material meshes.", parameterName);

    readonly record struct PositionBits(int X, int Y, int Z)
    {
        internal PositionBits(Vector3 position) : this(Bits(position.X), Bits(position.Y), Bits(position.Z)) { }

        // Negative zero is the same point as zero, so it joins the same weld, as the GLB policy keys it.
        static int Bits(float coordinate) => BitConverter.SingleToInt32Bits(coordinate == 0f ? 0f : coordinate);
    }

    struct WeldedVertex(Vector3 position, Vector3 direction, float bearing)
    {
        float? below;
        // The body admits at most 128 regions. Track only touched regions with a visible standoff to update.
        UInt128 standoffRegions;

        internal Vector3 Position { get; private set; } = position;

        internal void Touch(float? heightBelow, UInt128 standoffBit)
        {
            if (heightBelow is { } height)
                below = MathF.Max(below ?? 0, height);
            standoffRegions |= standoffBit;
        }

        internal bool Lift(float clearance, Span<float> largestLift)
        {
            if (below is not { } height)
                return false;
            float distance = MathF.Max(0, height + clearance - bearing);
            if (distance == 0)
                return false;

            // Normalize the authored direction without rounding the offset before adding it to the source.
            double x = direction.X, y = direction.Y, z = direction.Z;
            double scale = distance / Math.Sqrt(x * x + y * y + z * z);
            Vector3 liftedPosition = new((float)(Position.X + x * scale), (float)(Position.Y + y * scale),
                (float)(Position.Z + z * scale));
            // A positive lift can round away when added to the source position.
            if (new PositionBits(liftedPosition) == new PositionBits(Position))
                return false;
            Position = liftedPosition;
            // Visit only the standoff regions this weld touches, clearing the lowest bit each time.
            for (UInt128 regions = standoffRegions; regions != UInt128.Zero; regions &= regions - UInt128.One)
            {
                int region = (int)UInt128.TrailingZeroCount(regions);
                largestLift[region] = MathF.Max(largestLift[region], distance);
            }
            return true;
        }
    }
}

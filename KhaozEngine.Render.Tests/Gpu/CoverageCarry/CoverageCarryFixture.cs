using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The unchanged public F0 geometry. Each actor has its own identity, frame and anchor.</summary>
internal sealed class CoverageCarryFixture
{
    internal CoverageCostGeometry Geometry { get; } = CoverageCostGeometry.Build(CoverageCostFixtureMode.Typical);
    readonly CoverageSupportSurface[] supports;
    internal CoverageRefinement Topology { get; }

    internal CoverageCarryFixture(bool zeroWords = false)
    {
        Topology = Geometry.Refinement;
        if (zeroWords)
        {
            var source = Geometry.Source;
            var skin = source.SkinVertices.ToArray();
            for (int i = 0; i < skin.Length; i++)
            {
                skin[i].Color.X = BitConverter.UInt32BitsToSingle(0x80000000u);
                skin[i].Uv.Y = BitConverter.UInt32BitsToSingle(0x80800000u);
            }
            var changed = new CoverageSourceTopology(source.Identity, source.SourceSha256, source.Positions, source.Vertices,
                source.Triangles, skin, source.Influences, source.Occurrences, source.Materials);
            Topology = CoverageRefinement.Build(changed, Geometry.Map);
        }
        var equipment = GltfLoader.LoadCoverageSource(Geometry.EquipmentGlb(), "coverage-carry-f0");
        supports = Geometry.Commands.Select(c => CoverageSupportSurface.Compile(equipment,
            c.InwardSupport.Select(r => new CoverageTriangleRange(r.Mesh, r.Primitive, r.FirstTriangle, r.Count)).ToArray(),
            c.CoverageLocal)).ToArray();
    }

    internal CoverageCommandPose[] Commands(int actor, Matrix4x4 root, float weight = 1) => Geometry.Commands.Select((c, i) =>
        new CoverageCommandPose(c.Id, (ulong)actor + 1, Topology, i, supports[i], root, supports[i].CoverageLocal * root,
            c.MinZ, c.MaxZ, c.CollarWidth, c.Clearance, c.MaxWorldRecession, weight)).ToArray();

    internal static Matrix4x4[] Palette(int pose)
    {
        var result = new Matrix4x4[2];
        CoverageCostGeometry.Pose(pose, result);
        return result;
    }
}

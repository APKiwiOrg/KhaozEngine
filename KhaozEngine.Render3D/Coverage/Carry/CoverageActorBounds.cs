using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>Cold source/scalar envelopes and allocation-free conservative posed bounds. No posed geometry queries.</summary>
internal sealed class CoverageActorBounds
{
    readonly double x, y, z, weight = 1, scalar;
    internal CoverageActorBounds(CoverageRefinement topology, CoverageRecessionTable table)
    {
        foreach (var vertex in topology.Source.SkinVertices)
        {
            x = Math.Max(x, Math.Abs((double)vertex.Position.X));
            y = Math.Max(y, Math.Abs((double)vertex.Position.Y));
            z = Math.Max(z, Math.Abs((double)vertex.Position.Z));
            weight = Math.Max(weight, Math.Abs((double)vertex.BoneWeights.X) + Math.Abs((double)vertex.BoneWeights.Y)
                + Math.Abs((double)vertex.BoneWeights.Z) + Math.Abs((double)vertex.BoneWeights.W));
        }
        for (int row = 0; row < table.EntryCount; row++)
            foreach (float value in table.Entry(row)) scalar = Math.Max(scalar, Math.Abs((double)value));
    }
    internal PointShadowCasterSphere Sphere(CoveragePreparation p)
    {
        double bx = x, by = y, bz = z;
        foreach (var m in p.Owner.Uniforms.ComposedPalette)
        {
            bx = Math.Max(bx, weight * (x * Math.Abs(m.M11) + y * Math.Abs(m.M21) + z * Math.Abs(m.M31) + Math.Abs(m.M41)));
            by = Math.Max(by, weight * (x * Math.Abs(m.M12) + y * Math.Abs(m.M22) + z * Math.Abs(m.M32) + Math.Abs(m.M42)));
            bz = Math.Max(bz, weight * (x * Math.Abs(m.M13) + y * Math.Abs(m.M23) + z * Math.Abs(m.M33) + Math.Abs(m.M43)));
        }
        if (!p.Raw)
            foreach (var command in p.Owner.Uniforms.Commands)
            {
                bx += scalar * Math.Abs(command.RecessionDirection.X);
                by += scalar * Math.Abs(command.RecessionDirection.Y);
                bz += scalar * Math.Abs(command.RecessionDirection.Z);
            }
        var model = p.Model;
        double wx = bx * Math.Abs(model.M11) + by * Math.Abs(model.M21) + bz * Math.Abs(model.M31);
        double wy = bx * Math.Abs(model.M12) + by * Math.Abs(model.M22) + bz * Math.Abs(model.M32);
        double wz = bx * Math.Abs(model.M13) + by * Math.Abs(model.M23) + bz * Math.Abs(model.M33);
        float radius = MathF.BitIncrement((float)(Math.Sqrt(wx * wx + wy * wy + wz * wz) * 1.00001));
        return new(new(model.M41, model.M42, model.M43), radius);
    }
}

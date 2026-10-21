using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Independent CPU tight-bound observation in the GPU's monotonic float-word representation.</summary>
internal static class CoverageCarryBounds
{
    internal static uint[] Of(ReadOnlySpan<ModelVertex> vertices)
    {
        uint[] bounds = [uint.MaxValue, uint.MaxValue, uint.MaxValue, 0, 0, 0];
        Span<float> position = stackalloc float[3];
        foreach (var v in vertices)
        {
            position[0] = v.Position.X; position[1] = v.Position.Y; position[2] = v.Position.Z;
            for (int axis = 0; axis < 3; axis++)
            {
                uint bits = BitConverter.SingleToUInt32Bits(position[axis]);
                uint ordered = (bits & 0x80000000u) != 0 ? ~bits : bits ^ 0x80000000u;
                bounds[axis] = Math.Min(bounds[axis], ordered);
                bounds[axis + 3] = Math.Max(bounds[axis + 3], ordered);
            }
        }
        return bounds;
    }
}

using System;

namespace KhaozEngine.Render3D
{
    /// <summary>Shared index validation for imported and caller-supplied meshes.</summary>
    internal static class MeshIndexValidation
    {
        public static int Source(int index, int vertexCount, string identity)
        {
            if ((uint)index >= (uint)vertexCount)
                throw new InvalidOperationException(
                    $"{identity} has index {index} outside vertex count {vertexCount}.");
            return index;
        }

        public static void All(GltfMesh mesh, string identity)
        {
            int vertexCount = mesh.Vertices.Length;
            foreach (uint index in mesh.Indices32)
                if (index >= (uint)vertexCount)
                    throw new InvalidOperationException(
                        $"{identity} has index {index} outside vertex count {vertexCount}.");
        }

        public static uint Rebase(int index, int vertexCount, long baseIndex, string identity)
        {
            Source(index, vertexCount, identity);
            long rebased;
            try { rebased = checked(baseIndex + index); }
            catch (OverflowException ex)
            {
                throw new InvalidOperationException(
                    $"{identity} index {index} overflows while rebasing from vertex {baseIndex}.", ex);
            }
            if ((ulong)rebased > uint.MaxValue)
                throw new InvalidOperationException(
                    $"{identity} index {index} overflows while rebasing from vertex {baseIndex}.");
            return (uint)rebased;
        }
    }
}

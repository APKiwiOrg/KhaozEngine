using System;

namespace KhaozEngine.Render3D
{
    /// <summary>Shared index validation for imported and caller-supplied meshes.</summary>
    internal static class MeshIndexValidation
    {
        public static int Source(int index, int vertexCount, string identity, string? entryId = null,
                                 int? partIndex = null, string? diagnosticIdentity = null)
        {
            if ((uint)index >= (uint)vertexCount)
                throw new MeshIndexStateException(MeshIndexRefusal.SourceIndexOutsideVertexCount,
                    $"{diagnosticIdentity ?? identity} has index {index} outside vertex count {vertexCount}.")
                { Identity = identity, Index = index, VertexCount = vertexCount, EntryId = entryId, PartIndex = partIndex };
            return index;
        }

        public static void All(GltfMesh mesh, string identity, string? entryId = null,
                               int? partIndex = null, string? diagnosticIdentity = null)
        {
            int vertexCount = mesh.Vertices.Length;
            foreach (uint index in mesh.Indices32)
                if (index >= (uint)vertexCount)
                    throw new MeshIndexStateException(MeshIndexRefusal.SourceIndexOutsideVertexCount,
                        $"{diagnosticIdentity ?? identity} has index {index} outside vertex count {vertexCount}.")
                    { Identity = identity, Index = index, VertexCount = vertexCount, EntryId = entryId, PartIndex = partIndex };
        }

        public static uint Rebase(int index, int vertexCount, long baseIndex, string identity,
                                  string? entryId = null, int? partIndex = null, string? diagnosticIdentity = null)
        {
            Source(index, vertexCount, identity, entryId, partIndex, diagnosticIdentity);
            long rebased;
            try { rebased = checked(baseIndex + index); }
            catch (OverflowException ex)
            {
                throw new MeshIndexStateException(MeshIndexRefusal.RebasedIndexOverflow,
                    $"{diagnosticIdentity ?? identity} index {index} overflows while rebasing from vertex {baseIndex}.", ex)
                {
                    Identity = identity,
                    Index = index,
                    VertexCount = vertexCount,
                    BaseIndex = baseIndex,
                    EntryId = entryId,
                    PartIndex = partIndex
                };
            }
            if ((ulong)rebased > uint.MaxValue)
                throw new MeshIndexStateException(MeshIndexRefusal.RebasedIndexOverflow,
                    $"{diagnosticIdentity ?? identity} index {index} overflows while rebasing from vertex {baseIndex}.")
                {
                    Identity = identity,
                    Index = index,
                    VertexCount = vertexCount,
                    BaseIndex = baseIndex,
                    EntryId = entryId,
                    PartIndex = partIndex
                };
            return (uint)rebased;
        }
    }
}

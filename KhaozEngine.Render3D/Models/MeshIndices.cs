using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Shared helpers for the 32-bit-capable mesh index storage used by <see cref="GltfMesh"/> and
    /// <see cref="SkinnedGltfMesh"/>. Indices are stored authoritatively as <c>uint[]</c>; the GPU index-buffer
    /// width is chosen per mesh from the largest index value so small meshes keep their byte-identical 16-bit
    /// index buffers and only meshes past the 65,536-vertex ceiling pay for 32-bit indices.
    /// </summary>
    internal static class MeshIndices
    {
        /// <summary>UInt16 when every index fits in 16 bits (max index &lt;= 65535), else UInt32. Empty =&gt; UInt16.</summary>
        public static GpuIndexFormat ChooseFormat(uint[] indices)
        {
            uint max = 0;
            for (int i = 0; i < indices.Length; i++)
                if (indices[i] > max) max = indices[i];
            return max > ushort.MaxValue ? GpuIndexFormat.UInt32 : GpuIndexFormat.UInt16;
        }

        /// <summary>Widen 16-bit indices to the authoritative 32-bit storage.</summary>
        public static uint[] Widen(ushort[] indices)
        {
            var dst = new uint[indices.Length];
            for (int i = 0; i < indices.Length; i++) dst[i] = indices[i];
            return dst;
        }

        /// <summary>Narrow 32-bit indices back to 16 bits. Only valid when the mesh fits (caller checks the format).</summary>
        public static ushort[] Narrow(uint[] indices)
        {
            var dst = new ushort[indices.Length];
            for (int i = 0; i < indices.Length; i++) dst[i] = (ushort)indices[i];
            return dst;
        }
    }
}

using System;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A lightweight handle to a mesh uploaded to the GPU via <see cref="Scene3D.LoadMesh(KhaozEngine.Render3D.GltfMesh)"/>. Carries a slot
    /// <see cref="Index"/> plus a <see cref="Generation"/> so a handle held after <see cref="Scene3D.UnloadMesh"/>
    /// (a stale handle) is detectably invalid even if its slot index gets reused. A <c>default</c> handle has
    /// <see cref="Generation"/> 0 and is never valid; live handles start at generation 1.
    /// </summary>
    public readonly struct MeshHandle : IEquatable<MeshHandle>
    {
        public int Index { get; }
        public int Generation { get; }

        public MeshHandle(int index, int generation) { Index = index; Generation = generation; }

        /// <summary>Index-only ctor (generation 1). Retained for opaque/test construction; prefer the two-arg
        /// form from <see cref="Scene3D.LoadMesh(KhaozEngine.Render3D.GltfMesh)"/>.</summary>
        public MeshHandle(int index) : this(index, 1) { }

        public bool Equals(MeshHandle other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object? obj) => obj is MeshHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Index, Generation);
    }
}

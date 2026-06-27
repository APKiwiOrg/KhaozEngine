namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A lightweight handle to a mesh uploaded to the GPU via <see cref="Scene3D.LoadMesh(KhaozEngine.Render3D.GltfMesh)"/>. Carries a slot
    /// <see cref="Index"/> plus a <see cref="Generation"/> so a handle held after <see cref="Scene3D.UnloadMesh"/>
    /// (a stale handle) is detectably invalid even if its slot index gets reused. A <c>default</c> handle has
    /// <see cref="Generation"/> 0 and is never valid; live handles start at generation 1.
    /// </summary>
    public readonly struct MeshHandle
    {
        public int Index { get; }
        public int Generation { get; }

        public MeshHandle(int index, int generation) { Index = index; Generation = generation; }

        /// <summary>Index-only ctor (generation 1). Retained for opaque/test construction; prefer the two-arg
        /// form from <see cref="Scene3D.LoadMesh(KhaozEngine.Render3D.GltfMesh)"/>.</summary>
        public MeshHandle(int index) : this(index, 1) { }
    }
}

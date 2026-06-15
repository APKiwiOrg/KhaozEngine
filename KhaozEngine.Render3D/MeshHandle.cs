namespace KhaozEngine.Render3D
{
    /// <summary>A lightweight handle to a mesh uploaded to the GPU via <see cref="Scene3D.LoadMesh"/>.</summary>
    public readonly struct MeshHandle
    {
        public int Index { get; }
        public MeshHandle(int index) { Index = index; }
    }
}

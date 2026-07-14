namespace KhaozEngine.Render3D
{
    /// <summary>How a debug wire volume (<see cref="Scene3D.DebugWireSphere"/> / <see cref="Scene3D.DebugWireDome"/> /
    /// <see cref="Scene3D.DebugWireCylinder"/> / <see cref="Scene3D.DebugWireCircle"/>) is composited against the 3D
    /// scene.</summary>
    public enum DebugDepthMode
    {
        /// <summary>Depth-tested and drawn in-world before the post chain, so terrain, props, and other geometry
        /// occlude the parts of the volume behind them (the default: a volume reads as a shape sitting in the world,
        /// and flows through the post chain like the meshes).</summary>
        DepthTested,

        /// <summary>Drawn crisp on top of the finished frame with no depth test, so the whole volume is always visible
        /// through geometry. Useful when a volume is fully buried (an underground trigger, a boss hull inside terrain)
        /// and you still need to see its full outline.</summary>
        AlwaysOnTop,
    }
}

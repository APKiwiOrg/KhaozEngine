namespace KhaozEngine.Render3D
{
    /// <summary>
    /// How the water surface's vertex grid is laid out under a queued <see cref="WaterPlane"/>. The SHADING stack
    /// is identical either way, and so is the wave source: this only picks where the vertices sit and how they
    /// move (or do not move) as the camera does.
    /// </summary>
    public enum WaterGridMode
    {
        /// <summary>
        /// The camera-focused warped grid: a fixed vertex budget over the whole plane, redistributed toward the
        /// camera's XZ by <see cref="WaterSettings.GridFocusBias"/>. Everything shipped through 16.6.0, unchanged,
        /// and the default.
        /// <para>
        /// Its known cost is that the mesh is CAMERA-RELATIVE, so every vertex translates rigidly with the camera
        /// and slides through the wave field as it moves. Measured on the FFT ocean
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/296">#296</see>): a 0.10 m camera step at
        /// frozen wave time changes the rendered height field by roughly 85 per cent of the field's own legitimate
        /// per-frame motion at running speed, and 3.7x it at a sprint. It reads as the sea boiling in place.
        /// <see cref="Clipmap"/> is the fix.
        /// </para>
        /// </summary>
        CameraFocused = 0,

        /// <summary>
        /// A world-locked clipmap: concentric square rings, each with its own world-space cell size (doubling
        /// outward), every vertex snapped to its own ring's lattice in WORLD space. Between snaps no vertex moves
        /// at all, and a snap moves a ring by a whole number of its own cells, which maps lattice points onto
        /// lattice points - so the rendered surface over the overlap is unchanged rather than resampled. Combined
        /// with the per-ring band limit it drives (each ring low-passes the cascade maps to its own Nyquist, which
        /// needs the mipped maps of
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/344">#344</see>), this is what removes the
        /// boiling.
        /// <para>
        /// Opt-in for now: the default stays <see cref="CameraFocused"/> so every existing consumer renders
        /// byte-identically. Sized by <see cref="WaterSettings.ClipmapCellSize"/>,
        /// <see cref="WaterSettings.ClipmapRingCells"/> and <see cref="WaterSettings.ClipmapLevels"/>. At the
        /// defaults it is CHEAPER than <see cref="CameraFocused"/> as well as steadier (fewer triangles, and the
        /// buffers are rebuilt only when a ring actually snaps rather than every frame).
        /// </para>
        /// <para>
        /// The trade it makes instead: coverage is centred on the camera rather than on the plane, so a plane much
        /// larger than the outermost ring is only drawn out to that ring. <see cref="WaterSettings.ClipmapLevels"/>
        /// left at 0 sizes the ring count from the plane automatically, which is the intended way to use it.
        /// </para>
        /// </summary>
        Clipmap = 1,
    }
}

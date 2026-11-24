using System;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>A first-use covered opaque output could not join the optional AO layout.</summary>
internal sealed class AmbientCoverageOutputException(Exception inner)
    : InvalidOperationException("Could not prepare the coverage shaders for the ambient occlusion target.", inner);

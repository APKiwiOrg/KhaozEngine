using System;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageGpuScene
{
    CoverageSceneSurfaceEvidence[] lastSurfaces = [];

    byte[] EvidenceFrame(Action<Scene3D, int> draw, Func<SkinnedCoverageFrame[]> frames)
    {
        using var evidence = new CoverageSceneEvidence(Fixture);
        byte[] colour = evidence.Frame(draw, frames);
        lastSurfaces = evidence.Surfaces;
        return colour;
    }

    CoverageSceneSurfaceEvidence Surface(SkinnedCoverageFrame frame) =>
        lastSurfaces.Single(surface => surface.PreparationId == frame.Preparation!.Id);
}

using System;
using System.Collections.Generic;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

public static partial class ShippedShaderPrograms
{
    /// <summary>Base programs plus every layout-selected lit or destination-preserving visibility variant.</summary>
    public static IEnumerable<ShippedGraphicsProgram> GraphicsPrograms()
    {
        foreach (ShippedGraphicsProgram program in BaseGraphicsPrograms())
        {
            yield return program;
            if (program.Name.StartsWith("Temporal", StringComparison.Ordinal)
                && program.FragmentGlsl.Contains("bool visibilityReleased = ", StringComparison.Ordinal))
                yield return new(program.Name + "FullyLit", program.VertexGlsl,
                    TemporalVisibilityPrograms.WithoutDecision(program.FragmentGlsl));
        }
        foreach (ShippedGraphicsProgram program in BaseGraphicsPrograms())
        {
            bool lit = program.Name is "Model" or "Foliage" or "ModelDissolve" or "SkinnedModel"
                or "SkinnedModelDissolve" or "Splat" or "TileGround" or "ModelMotion" or "SkinnedModelMotion"
                or "SkinnedModelDissolveMotion" or "ModelCpuSkinnedMotion" or "ModelCpuSkinnedDissolveMotion"
                or "FoliageMotion" or "SplatMotion" or "TileGroundMotion";
            bool preserve = program.Name is "TexturedBillboard" or "Beam" or "Trail" or "OverlayMesh"
                or "TexturedBillboardMotion" or "BeamMotion" or "TrailMotion" or "OverlayMeshMotion" or "SilhouetteMotion";
            if (!lit && !preserve) continue;
            yield return new(program.Name + "Visibility", program.VertexGlsl,
                ShadowVisibilityTargets.Fragment(program.FragmentGlsl,
                    program.Name.Contains("Motion", StringComparison.Ordinal), preserve), program.WaterVolumeBinding);
        }
        yield return new("SilhouetteVisibility", ShaderSources.SilhouetteVert,
            ShadowVisibilityTargets.Fragment(ShaderSources.SilhouetteFrag, false, preserve: true));
    }
}

using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Bounds the full source geometry rebuilt by the seam validator, including absent-cell corners.</summary>
internal static class MapTerrainSeamValidation
{
    internal static bool IsValid(MapSurfaceSeam seam, MapScopedSurfaces view, MapSceneAdmission admission)
    {
        using var workspace = admission.Workspace();
        long bytes = checked(4096L + PatchBytes(seam.First.Patch, view) + PatchBytes(seam.Second.Patch, view) +
            512L * seam.Pairs.Count);
        admission.BeforeGeometry(bytes, workspace);
        return MapSeamValidator.Validate(seam, view).Count == 0;
    }

    static long PatchBytes(MapPatchKey key, MapScopedSurfaces view)
    {
        if (!view.TryAcquiredPatch(key, out var patch, out var status) || status != MapPatchStatus.Present || patch is null) return 0;
        long vertices = checked(((long)patch.Width + 1) * (patch.Depth + 1));
        for (int z = 0; z < patch.Depth; z++)
            for (int x = 0; x < patch.Width; x++)
                if (patch.IsPresent(x, z)) vertices = checked(vertices + 4);
        foreach (var edge in patch.EdgeSubdivisions) vertices = checked(vertices + edge.Segments - 1);
        // At most four legacy midpoints per present cell, plus every authored subdivision vertex.
        // The envelope covers growing source/edge dictionaries, pair sets and local-validation scratch.
        return checked(16L * patch.Cells.Length + 512L * vertices +
            128L * (patch.CornerDependencies.Count + patch.EdgeSubdivisions.Count));
    }
}

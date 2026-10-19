using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.Primitives;

namespace KhaozEngine.TileWorld;

/// <summary>Mesh-local roof samples retained across detached builds. Use on one build thread.</summary>
internal sealed class TileInteriorRoofSamples
{
    readonly ConditionalWeakTable<ClearanceMesh, Dictionary<float, ClearanceSamples>> _samples = new();

    internal ClearanceSamples? Get(ITileMeshGeometry meshes, string meshRef, float cellSize, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(meshRef))
        {
            error = "the archetype has no meshRef";
            return null;
        }
        try
        {
            // Always ask the reader again so its file-stamp check can replace a re-exported mesh.
            if (!meshes.TryLoad(meshRef, out ClearanceMesh? mesh, out error))
            {
                error ??= "the mesh source could not read the mesh";
                return null;
            }
            if (mesh is null)
            {
                error = "the mesh source reported success but returned no mesh";
                return null;
            }
            Dictionary<float, ClearanceSamples> pitches = _samples.GetValue(mesh, static _ => new());
            if (!pitches.TryGetValue(cellSize, out ClearanceSamples? samples))
                pitches.Add(cellSize, samples = ClearanceSamples.Build(mesh, cellSize / 2f));
            error = null;
            return samples;
        }
        catch (ArgumentException e)
        {
            error = e.Message;
            return null;
        }
    }
}

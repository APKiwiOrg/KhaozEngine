using System;
using System.Numerics;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal;

/// <summary>A clipped rectangle and its atlas layer. Depth comes from attenuation, never the body's floor.</summary>
internal readonly record struct WaterCausticBody(int PlaneIndex, int Layer, float Strength, float Depth,
    Vector2 RectMin, Vector2 RectMax);

/// <summary>Reusable CPU frame plan. It neither creates GPU resources nor records render work.</summary>
internal sealed class WaterCausticPlan
{
    internal const int MaxLayers = 4;
    const int PatchCells = 32, Slices = 4;
    const int PatchesPerSide = WaterSurfaceWindow.Cells / PatchCells;
    const float PatchMetres = PatchCells * WaterSurfaceWindow.CellMetres;
    readonly WaterSettings _scratch = new();
    WaterCausticBody[] _bodies = Array.Empty<WaterCausticBody>();
    int[] _planeBodies = Array.Empty<int>();
    int _bodyCount, _planeCount;

    internal bool Active => _bodyCount > 0;
    internal WaterSurfaceWindow Window { get; private set; }
    internal int LayerCount { get; private set; }
    internal ReadOnlySpan<WaterCausticBody> Bodies => _bodies.AsSpan(0, _bodyCount);
    internal WaterCausticDiagnostics Diagnostics { get; private set; }

    internal int LayerOfPlane(int planeIndex)
    {
        int index = BodyIndex(planeIndex);
        return index < 0 ? 0 : _bodies[index].Layer + 1;
    }

    internal float DepthOfPlane(int planeIndex)
    {
        int index = BodyIndex(planeIndex);
        return index < 0 ? 0f : _bodies[index].Depth;
    }

    int BodyIndex(int planeIndex) => (uint)planeIndex < (uint)_planeCount ? _planeBodies[planeIndex] - 1 : -1;

    /// <summary>Body opt-in, independent of this frame's window and sun. Resolved authoring is already validated.</summary>
    internal static bool Participates(in WaterPlane plane, WaterSettings resolved) =>
        resolved.UnderwaterCaustics && resolved.CausticStrength > 0f
        && WaterRenderer.EffectiveWaveSource(plane, resolved) == WaterWaveSource.Procedural;

    /// <summary>Deepest of the four map slices, where direct sunlight has attenuated by about exp(-3).</summary>
    internal static float SliceDepth(Vector3 attenuationPerMetre)
    {
        double mean = ((double)attenuationPerMetre.X + attenuationPerMetre.Y + attenuationPerMetre.Z) / 3d;
        return mean > 0d ? (float)Math.Clamp(3d / mean, 1d, 32d) : 32f;
    }

    /// <summary>Conservative atlas capacity, ignoring the camera window and sun. Atlas sizes are 1, 2 or 4.</summary>
    internal static int RequiredLayers(ReadOnlySpan<WaterPlane> planes, WaterSettings scene, WaterSettings scratch)
    {
        int count = 0;
        foreach (ref readonly var plane in planes)
        {
            Resolve(plane, scene, scratch);
            if (Participates(plane, scratch) && ++count > 2) return MaxLayers;
        }
        return count;
    }

    /// <summary>Write window-local patch corners in lattice cells and zero-based slice indices.</summary>
    internal static int WritePatches(in WaterCausticBody body, in WaterSurfaceWindow window, Span<Vector3> instances)
    {
        var windowMin = new Vector2(window.MinX, window.MinZ);
        var minimum = Vector2.Max(body.RectMin, windowMin);
        var maximum = Vector2.Min(body.RectMax, windowMin + new Vector2(WaterSurfaceWindow.ExtentMetres));
        if (!(minimum.X < maximum.X && minimum.Y < maximum.Y)) return 0;

        int firstX = PatchIndex(minimum.X - window.MinX, false);
        int firstZ = PatchIndex(minimum.Y - window.MinZ, false);
        int endX = PatchIndex(maximum.X - window.MinX, true);
        int endZ = PatchIndex(maximum.Y - window.MinZ, true);
        int count = (endX - firstX) * (endZ - firstZ) * Slices;
        if (instances.Length < count)
            throw new ArgumentException("The destination must hold every patch slice for this body.", nameof(instances));
        int written = 0;
        for (int slice = 0; slice < Slices; slice++)
            for (int z = firstZ; z < endZ; z++)
                for (int x = firstX; x < endX; x++)
                    instances[written++] = new Vector3(x * PatchCells, z * PatchCells, slice);
        return written;
    }

    static int PatchIndex(float offset, bool end) => Math.Clamp(
        (int)(end ? MathF.Ceiling(offset / PatchMetres) : MathF.Floor(offset / PatchMetres)), 0, PatchesPerSide);

    internal void Build(ReadOnlySpan<WaterPlane> relativePlanes, WaterSettings scene, Vector3 lightDirection,
        Vector3 eyeRender, Vector3 forward, Matrix4x4 viewProjection, bool orthographic, Vector3 renderOrigin)
    {
        _bodyCount = _planeCount = LayerCount = 0;
        Window = default;
        int optedIn = 0, fft = 0, overflow = 0;
        double nearest = double.PositiveInfinity;
        float surfaceY = 0f;
        foreach (ref readonly var plane in relativePlanes)
        {
            Resolve(plane, scene, _scratch);
            if (_scratch.UnderwaterCaustics)
            {
                optedIn++;
                if (WaterRenderer.EffectiveWaveSource(plane, _scratch) != WaterWaveSource.Procedural) fft++;
            }
            if (!Participates(plane, _scratch) || !Bounds(plane, out var minimum, out var maximum)) continue;
            // Distance to the rectangle matters: the closest centre can belong to a more distant body.
            double dx = Math.Max(0d, Math.Max((double)minimum.X - eyeRender.X, (double)eyeRender.X - maximum.X));
            double dz = Math.Max(0d, Math.Max((double)minimum.Y - eyeRender.Z, (double)eyeRender.Z - maximum.Y));
            double distance = dx * dx + dz * dz;
            if (distance < nearest)
            {
                nearest = distance;
                surfaceY = plane.SurfaceY;
            }
        }
        Diagnostics = new(optedIn, 0, fft, 0, 0);
        if (!(lightDirection.Y < 0f) || double.IsPositiveInfinity(nearest)) return;

        Window = WaterSurfaceWindow.Place(eyeRender, forward, viewProjection, orthographic, surfaceY, renderOrigin);
        EnsureCapacity(relativePlanes.Length);
        _planeCount = relativePlanes.Length;
        Array.Clear(_planeBodies, 0, _planeCount);
        var windowMin = new Vector2(Window.MinX, Window.MinZ);
        var windowMax = windowMin + new Vector2(WaterSurfaceWindow.ExtentMetres);
        for (int i = 0; i < relativePlanes.Length; i++)
        {
            ref readonly var plane = ref relativePlanes[i];
            Resolve(plane, scene, _scratch);
            if (!Participates(plane, _scratch) || !Bounds(plane, out var minimum, out var maximum)) continue;
            minimum = Vector2.Max(minimum, windowMin);
            maximum = Vector2.Min(maximum, windowMax);
            if (!(minimum.X < maximum.X && minimum.Y < maximum.Y)) continue;
            int layer = FindLayer(relativePlanes, plane.Look, minimum, maximum);
            if (layer == MaxLayers)
            {
                overflow++;
                continue;
            }
            Vector3 attenuation;
            if (_scratch.WaterColumnLight)
                attenuation = WaterColumnOptics.Derive(_scratch.WaterColor, _scratch.Clarity).Extinction;
            else
            {
                var absorption = _scratch.AbsorptionPerMetre;
                attenuation = new Vector3(absorption.R, absorption.G, absorption.B);
            }
            _bodies[_bodyCount++] = new(i, layer, _scratch.CausticStrength, SliceDepth(attenuation), minimum, maximum);
            _planeBodies[i] = _bodyCount;
            LayerCount = Math.Max(LayerCount, layer + 1);
        }
        Diagnostics = new(optedIn, _bodyCount, fft, overflow, LayerCount);
    }

    int FindLayer(ReadOnlySpan<WaterPlane> planes, WaterLook? look, Vector2 minimum, Vector2 maximum)
    {
        for (int layer = 0; layer < MaxLayers; layer++)
        {
            bool conflict = false;
            foreach (ref readonly var body in Bodies)
            {
                if (body.Layer != layer) continue;
                // Both rectangles grow by one cell when their look references differ.
                float padding = ReferenceEquals(look, planes[body.PlaneIndex].Look) ? 0f : 2f * WaterSurfaceWindow.CellMetres;
                if (minimum.X < body.RectMax.X + padding && maximum.X > body.RectMin.X - padding
                    && minimum.Y < body.RectMax.Y + padding && maximum.Y > body.RectMin.Y - padding)
                {
                    conflict = true;
                    break;
                }
            }
            if (!conflict) return layer;
        }
        return MaxLayers;
    }

    static void Resolve(in WaterPlane plane, WaterSettings scene, WaterSettings scratch)
    {
        if (plane.Look is { } look) look.ResolveInto(scratch, scene);
        else scratch.CopyFrom(scene);
    }

    static bool Bounds(in WaterPlane plane, out Vector2 minimum, out Vector2 maximum)
    {
        minimum = new(plane.CenterX - plane.HalfExtentX, plane.CenterZ - plane.HalfExtentZ);
        maximum = new(plane.CenterX + plane.HalfExtentX, plane.CenterZ + plane.HalfExtentZ);
        return plane.HalfExtentX > 0f && plane.HalfExtentZ > 0f && float.IsFinite(plane.SurfaceY)
            && float.IsFinite(minimum.X) && float.IsFinite(minimum.Y)
            && float.IsFinite(maximum.X) && float.IsFinite(maximum.Y);
    }

    void EnsureCapacity(int count)
    {
        if (_bodies.Length >= count) return;
        int capacity = Math.Max(count, Math.Max(4, _bodies.Length * 2));
        Array.Resize(ref _bodies, capacity);
        Array.Resize(ref _planeBodies, capacity);
    }
}

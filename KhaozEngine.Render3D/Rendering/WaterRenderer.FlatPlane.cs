using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// EVERY PLANE WITH NO VERTEX DISPLACEMENT, IN EITHER GRID MODE, IS ONE QUAD OF ONE SHARED VERTEX BUFFER. The
    /// frame writes all of its quads into a CPU mirror and uploads them in one write before the pass opens, so no
    /// quad costs an upload inside the pass. Each draw names its own quad by vertex offset over the one six-index
    /// buffer. The quad matches the zero-swell grid mathematically, since every varying the vertex stage writes is
    /// affine or constant there, but not byte for byte, and WaterFlatPlaneGoldenTests holds the difference.
    /// </summary>
    internal sealed partial class WaterRenderer
    {
        internal const uint FlatIndexCount = 6;

        /// <summary>Vertices in one quad.</summary>
        internal const int FlatQuadVertices = 4;

        /// <summary>Bytes in one quad: four positions of 12 bytes.</summary>
        internal const uint FlatQuadBytes = (uint)FlatQuadVertices * 12u;

        IGpuBuffer? _flatVb;
        IGpuBuffer? _flatIb;
        int _flatCapacity;   // quads the vertex buffer holds
        Vector3[] _flatVertices = Array.Empty<Vector3>();

        /// <summary>
        /// Whether a plane draws as a flat quad: its effective source is procedural and its effective swell does not
        /// displace (<see cref="WaterSwellReach.Displaces"/>). FFT planes keep their grid even when the procedural
        /// swell knobs are off because their displacement comes from the ocean maps.
        /// </summary>
        internal static bool UsesFlatQuad(in WaterPlane plane, WaterSettings settings)
            => EffectiveWaveSource(plane, settings) == WaterWaveSource.Procedural
                && !WaterSwellReach.Displaces(plane.Look?.SwellAmplitude ?? settings.SwellAmplitude,
                    plane.Look?.SwellWavelength ?? settings.SwellWavelength);

        /// <summary>Grow the quad buffer to hold <paramref name="quads"/>, geometrically. The replaced buffer is
        /// RETIRED rather than disposed because a submitted frame may still read it, the rule
        /// <see cref="EnsureUboCapacity"/> follows. The index buffer never changes after its first write.</summary>
        void EnsureFlatBuffers(int quads)
        {
            if (_flatIb is null)
            {
                _flatIb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                    FlatIndexCount * sizeof(uint), GpuBufferUsage.IndexBuffer));
                _gd.UpdateBuffer<uint>(_flatIb, 0, [0, 2, 1, 1, 2, 3]);
            }
            if (_flatVb is not null && _flatCapacity >= quads) return;
            _flatCapacity = Math.Max(quads, _flatCapacity == 0 ? 4 : _flatCapacity * 2);
            if (_flatVb is not null) _retired.Retire(_flatVb);
            _flatVb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                (uint)_flatCapacity * FlatQuadBytes, GpuBufferUsage.VertexBuffer));
            _flatVertices = new Vector3[_flatCapacity * FlatQuadVertices];
        }

        /// <summary>Write every quad the frame routed into the mirror, in slot order, and upload them in one write.
        /// Runs before the pass's <c>SetFramebuffer</c>.</summary>
        void UploadFlatQuads(IGpuCommandList commands, ReadOnlySpan<WaterPlane> planes)
        {
            if (_flatCount == 0) return;
            for (int i = 0; i < planes.Length; i++)
            {
                if (_routes[i] != PlaneRoute.FlatQuad) continue;
                WaterPlane plane = planes[i];
                float minX = plane.CenterX - plane.HalfExtentX;
                float maxX = plane.CenterX + plane.HalfExtentX;
                float minZ = plane.CenterZ - plane.HalfExtentZ;
                float maxZ = plane.CenterZ + plane.HalfExtentZ;
                int v = _routeSlots[i] * FlatQuadVertices;
                _flatVertices[v] = new Vector3(minX, plane.SurfaceY, minZ);
                _flatVertices[v + 1] = new Vector3(maxX, plane.SurfaceY, minZ);
                _flatVertices[v + 2] = new Vector3(minX, plane.SurfaceY, maxZ);
                _flatVertices[v + 3] = new Vector3(maxX, plane.SurfaceY, maxZ);
            }
            commands.UpdateBuffer<Vector3>(_flatVb!, 0, _flatVertices.AsSpan(0, _flatCount * FlatQuadVertices));
        }

        void DisposeFlatBuffers()
        {
            _flatVb?.Dispose();
            _flatIb?.Dispose();
        }
    }
}

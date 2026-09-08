using System;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    internal sealed partial class WaterRenderer
    {
        internal const uint FlatIndexCount = 6;

        IGpuBuffer? _flatVb;
        IGpuBuffer? _flatIb;
        readonly Vector3[] _flatVertices = new Vector3[4];

        /// <summary>
        /// A clipmap plane with no effective procedural displacement is geometrically a rectangle. FFT planes
        /// keep their clipmap even when the procedural swell knob is zero because their displacement comes from
        /// the ocean maps.
        /// </summary>
        internal static bool UsesFlatQuad(in WaterPlane plane, WaterSettings settings)
            => EffectiveWaveSource(plane, settings) == WaterWaveSource.Procedural
                && (plane.Look?.SwellAmplitude ?? settings.SwellAmplitude) == 0f;

        static bool AnyFlatPlane(ReadOnlySpan<WaterPlane> planes, WaterSettings settings)
        {
            for (int i = 0; i < planes.Length; i++)
                if (UsesFlatQuad(planes[i], settings)) return true;
            return false;
        }

        static bool AnyDisplacedPlane(ReadOnlySpan<WaterPlane> planes, WaterSettings settings)
        {
            for (int i = 0; i < planes.Length; i++)
                if (!UsesFlatQuad(planes[i], settings)) return true;
            return false;
        }

        void EnsureFlatBuffers()
        {
            if (_flatVb is not null && _flatIb is not null) return;
            _flatVb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                4u * sizeof(float) * 3u, GpuBufferUsage.VertexBuffer));
            _flatIb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                FlatIndexCount * sizeof(uint), GpuBufferUsage.IndexBuffer));
            _gd.UpdateBuffer<uint>(_flatIb, 0, [0, 2, 1, 1, 2, 3]);
        }

        void DrawFlatPlane(IGpuCommandList commands, in WaterPlane plane)
        {
            float minX = plane.CenterX - plane.HalfExtentX;
            float maxX = plane.CenterX + plane.HalfExtentX;
            float minZ = plane.CenterZ - plane.HalfExtentZ;
            float maxZ = plane.CenterZ + plane.HalfExtentZ;
            _flatVertices[0] = new Vector3(minX, plane.SurfaceY, minZ);
            _flatVertices[1] = new Vector3(maxX, plane.SurfaceY, minZ);
            _flatVertices[2] = new Vector3(minX, plane.SurfaceY, maxZ);
            _flatVertices[3] = new Vector3(maxX, plane.SurfaceY, maxZ);
            commands.UpdateBuffer<Vector3>(_flatVb!, 0, _flatVertices);
            commands.SetPipeline(_pipe);
            commands.SetIndexBuffer(_flatIb!, GpuIndexFormat.UInt32);
            commands.SetVertexBuffer(0, _flatVb!);
            commands.DrawIndexed(FlatIndexCount, 1, 0, 0, 0);
        }

        void DisposeFlatBuffers()
        {
            _flatVb?.Dispose();
            _flatIb?.Dispose();
        }
    }
}

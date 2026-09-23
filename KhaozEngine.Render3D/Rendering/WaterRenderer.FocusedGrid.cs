using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// THE CAMERA-FOCUSED GRID AS ONE SLICE PER PLANE of one shared vertex buffer, uploaded before the pass opens
    /// and drawn through the one static index buffer at the slice's vertex offset. That is the clipmap's shape
    /// (WaterRenderer.ClipmapBuffers.cs) without its cache: this grid follows the camera, so every slice is rebuilt
    /// every frame. What changes is where the upload lands. Writing every plane into offset 0 of one buffer forced
    /// each upload inside the pass, between one plane's draw and the next, and a backend that stages uploads
    /// through a blit ended and reopened the pass once per plane.
    /// </summary>
    internal sealed partial class WaterRenderer
    {
        /// <summary>Vertices in one camera-focused grid slice.</summary>
        internal const int GridSliceVertices = WaterMath.GridResolution * WaterMath.GridResolution;

        /// <summary>Bytes in one slice: 9,409 positions of 12 bytes, the 113 KB one plane has always uploaded.</summary>
        internal const uint GridSliceBytes = (uint)GridSliceVertices * 12u;

        IGpuBuffer? _vb;
        IGpuBuffer? _ib;
        int _gridSlices;   // slices the vertex buffer holds
        // Heap-allocated once, not stackalloc'd per draw: at GridResolution 97 the position scratch is 113 KB, far
        // past what belongs on the stack, and the axis scratch sits beside it. One plane's worth, reused per slice.
        readonly Vector3[] _gridScratch = new Vector3[GridSliceVertices];
        readonly float[] _axisScratch = new float[2 * WaterMath.GridResolution];

        /// <summary>Camera-focused grids built and uploaded by the last <see cref="Draw"/>. Internal, for tests.</summary>
        internal int LastFocusedGridBuilds { get; private set; }

        /// <summary>Hold at least <paramref name="slices"/> slices, growing geometrically and retiring the replaced
        /// buffer. The index buffer is plane-local and written once.</summary>
        void EnsureGridBuffers(int slices)
        {
            if (_ib is null)
            {
                const uint icount = WaterMath.GridIndexCount;
                _ib = _gd.Factory.CreateBuffer(new GpuBufferDescription(icount * sizeof(uint), GpuBufferUsage.IndexBuffer));
                uint[] indices = new uint[icount];   // built once, then thrown away: the index layout never changes
                WaterMath.BuildGridIndices(indices);
                _gd.UpdateBuffer(_ib, 0, indices);
            }
            if (_vb is not null && _gridSlices >= slices) return;
            _gridSlices = Math.Max(slices, _gridSlices * 2);
            if (_vb is not null) _retired.Retire(_vb);
            _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                (uint)_gridSlices * GridSliceBytes, GpuBufferUsage.VertexBuffer));
        }

        /// <summary>Build every routed plane's grid into its own slice. Runs before the pass's
        /// <c>SetFramebuffer</c>, beside the flat quads and the clipmap slices.</summary>
        void UploadFocusedGrids(IGpuCommandList cl, ReadOnlySpan<WaterPlane> planes, Vector3 cameraPos, float focusBias)
        {
            for (int i = 0; i < planes.Length; i++)
            {
                if (_routes[i] != PlaneRoute.FocusedGrid) continue;
                // The grid concentrates its vertices around the camera's XZ (clamped inside the plane by
                // BuildGridPositions), so the fixed vertex budget lands where the displaced swell actually reads.
                int n = WaterMath.BuildGridPositions(planes[i], cameraPos.X, cameraPos.Z, focusBias,
                    _gridScratch, _axisScratch);
                cl.UpdateBuffer<Vector3>(_vb!, (uint)_routeSlots[i] * GridSliceBytes, _gridScratch.AsSpan(0, n));
                LastFocusedGridBuilds++;
            }
        }
    }
}

using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// THE FRAME'S PLANE ROUTING: which geometry each queued plane draws through, decided once before any geometry
    /// is built or uploaded, so the uploads can land before the pass's <c>SetFramebuffer</c>. A plane keeps its
    /// queue index for its uniform slot and, under the clipmap, for its cached slice. Flat quads and camera-focused
    /// grids take compact slots in their own shared buffers. Draws stay in queue order because the pass is alpha
    /// blended and overlapping planes composite in the order they were queued.
    /// </summary>
    internal sealed partial class WaterRenderer
    {
        /// <summary>The geometry one queued plane draws through this frame.</summary>
        internal enum PlaneRoute : byte
        {
            /// <summary>Not drawn this frame.</summary>
            Culled,
            /// <summary>No displacement: one quad of the shared flat buffer.</summary>
            FlatQuad,
            /// <summary>The camera-focused grid.</summary>
            FocusedGrid,
            /// <summary>The world-locked clipmap, through the plane's own slice of the clip buffers.</summary>
            Clipmap,
        }

        PlaneRoute[] _routes = Array.Empty<PlaneRoute>();
        int[] _routeSlots = Array.Empty<int>();
        int _flatCount, _gridCount, _clipCount;

        /// <summary>How the last <see cref="Draw"/> routed queued plane <paramref name="index"/>. Internal, for the
        /// tests that pin the routing without reading pixels.</summary>
        internal PlaneRoute LastRoute(int index) => _routes[index];

        /// <summary>Planes the last <see cref="Draw"/> skipped because the view could not reach them. Internal, for tests.</summary>
        internal int LastCulledPlanes { get; private set; }

        /// <summary>Route every queued plane and count each route. Returns how many planes will draw.</summary>
        int RoutePlanes(ReadOnlySpan<WaterPlane> planes, WaterSettings settings, in FrustumPlanes frustum)
        {
            if (_routes.Length < planes.Length)
            {
                int capacity = Math.Max(planes.Length, _routes.Length * 2);
                _routes = new PlaneRoute[capacity];
                _routeSlots = new int[capacity];
            }
            bool clipmap = settings.GridMode == WaterGridMode.Clipmap;
            _flatCount = _gridCount = _clipCount = 0;
            LastCulledPlanes = 0;
            for (int i = 0; i < planes.Length; i++)
            {
                if (!MayBeVisible(planes[i], settings, frustum))
                {
                    _routes[i] = PlaneRoute.Culled;
                    _routeSlots[i] = -1;
                    LastCulledPlanes++;
                }
                else if (UsesFlatQuad(planes[i], settings))
                {
                    _routes[i] = PlaneRoute.FlatQuad;
                    _routeSlots[i] = _flatCount++;
                }
                else if (clipmap)
                {
                    // The clipmap's slices and their cache are keyed by queue index, so a culled plane keeps its
                    // slice and comes back without a rebuild when nothing it depends on moved.
                    _routes[i] = PlaneRoute.Clipmap;
                    _routeSlots[i] = i;
                    _clipCount++;
                }
                else
                {
                    _routes[i] = PlaneRoute.FocusedGrid;
                    _routeSlots[i] = _gridCount++;
                }
            }
            return _flatCount + _gridCount + _clipCount;
        }

        /// <summary>
        /// Whether a plane may reach the view: its rectangle grown by how far its surface can move. A procedural
        /// plane's reach is its effective swell's (<see cref="WaterSwellReach.Of"/>). An ocean plane is never culled,
        /// because its displacement comes from the cascade maps and the CPU holds no bound on it.
        /// </summary>
        static bool MayBeVisible(in WaterPlane plane, WaterSettings settings, in FrustumPlanes frustum)
        {
            if (EffectiveWaveSource(plane, settings) != WaterWaveSource.Procedural) return true;
            WaterLook? look = plane.Look;
            Vector2 reach = WaterSwellReach.Of(look?.SwellAmplitude ?? settings.SwellAmplitude,
                look?.SwellWavelength ?? settings.SwellWavelength, look?.SwellSteepness ?? settings.SwellSteepness);
            return WaterSwellReach.MayBeVisible(plane, reach, frustum);
        }

        /// <summary>Bind what a route draws through. Called only when the route differs from the previous draw's,
        /// so a run of flat quads binds its buffers once.</summary>
        void BindRoute(IGpuCommandList cl, PlaneRoute route)
        {
            switch (route)
            {
                case PlaneRoute.FlatQuad:
                    cl.SetPipeline(_pipe);
                    cl.SetIndexBuffer(_flatIb!, GpuIndexFormat.UInt32);
                    cl.SetVertexBuffer(0, _flatVb!);
                    break;
                case PlaneRoute.FocusedGrid:
                    cl.SetPipeline(_pipe);
                    cl.SetIndexBuffer(_ib!, GpuIndexFormat.UInt32);
                    cl.SetVertexBuffer(0, _vb!);
                    break;
                case PlaneRoute.Clipmap:
                    cl.SetPipeline(_clipPipe!);
                    cl.SetIndexBuffer(_clipIb!, GpuIndexFormat.UInt32);
                    cl.SetVertexBuffer(0, _clipVb!);
                    break;
            }
        }

        /// <summary>One draw per routed plane, in queue order.</summary>
        void DrawRoutedPlanes(IGpuCommandList cl, int planeCount)
        {
            PlaneRoute bound = PlaneRoute.Culled;
            for (int i = 0; i < planeCount; i++)
            {
                PlaneRoute route = _routes[i];
                if (route == PlaneRoute.Culled) continue;
                if (route != bound)
                {
                    BindRoute(cl, route);
                    bound = route;
                }
                cl.SetGraphicsResourceSet(0, _set!, (uint)i * SlotBytes);
                switch (route)
                {
                    case PlaneRoute.FlatQuad:
                        cl.DrawIndexed(FlatIndexCount, 1, 0, _routeSlots[i] * FlatQuadVertices, 0);
                        break;
                    case PlaneRoute.Clipmap:
                        // Each plane reads its own slice: indexStart walks the index buffer, vertexOffset rebases
                        // the plane-local indices onto its own vertex block.
                        cl.DrawIndexed((uint)_clipSlots[i].IndexCount, 1,
                            (uint)(i * _clipSliceIndices), i * _clipSliceVerts, 0);
                        break;
                    case PlaneRoute.FocusedGrid:
                        cl.DrawIndexed((uint)WaterMath.GridIndexCount, 1, 0, _routeSlots[i] * GridSliceVertices, 0);
                        break;
                }
            }
        }
    }
}
